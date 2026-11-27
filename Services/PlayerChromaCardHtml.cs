namespace DynamicIsland.Services;

/// <summary>
/// Автономный Zero-Blur интерфейс OmniWave в стиле Chroma Card.
/// Без внешних CDN и системного или CSS-размытия.
/// </summary>
public static class PlayerChromaCardHtml
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>OmniWave</title>
  <style>
    @property --chroma-r {
      syntax: '<number>';
      inherits: true;
      initial-value: 255;
    }
    @property --chroma-g {
      syntax: '<number>';
      inherits: true;
      initial-value: 204;
    }
    @property --chroma-b {
      syntax: '<number>';
      inherits: true;
      initial-value: 0;
    }
    @property --ambient-strength {
      syntax: '<number>';
      inherits: true;
      initial-value: 1;
    }

    :root {
      --ow-bg-base: #111216;
      --ow-bg-elevated: #181A20;
      --ow-bg-hover: #1E2129;
      --ow-bg-input: #0D0E12;
      --ow-hairline: #23262F;
      --ow-hairline-soft: rgba(255, 255, 255, 0.06);

      --ow-text-primary: #F3F4F6;
      --ow-text-secondary: #9CA3AF;
      --ow-text-muted: #6B7280;

      --chroma-r: 255;
      --chroma-g: 204;
      --chroma-b: 0;
      --ambient-strength: 1;

      --src-ym: #FFCC00;
      --src-vk: #2688EB;
      --src-sc: #FF5500;
      --src-yt: #FF3B30;
      --src-sp: #1DB954;
      --src-au: #CC0FF0;
      --src-bc: #1DA0C3;

      --status-ok: #22C55E;
      --status-warn: #F59E0B;
      --status-err: #EF4444;

      --ow-titlebar-height: 36px;
      --ow-radius-window: 16px;
      --ow-radius-pill: 12px;
      --ow-radius-art: 10px;
      --ow-radius-control: 8px;

      --ow-font-sans: "Segoe UI Variable Text", "Segoe UI Variable", "Inter", "Segoe UI", system-ui, -apple-system, sans-serif;
      --ow-font-mono: "JetBrains Mono", "Cascadia Mono", "Consolas", ui-monospace, SFMono-Regular, monospace;

      --ow-t-micro: 150ms cubic-bezier(0.2, 0, 0, 1);
      --ow-t-chroma: 450ms ease;
    }

    html.ow-win11 {
      --ow-radius-window: 8px;
      --ow-radius-pill: 8px;
    }

    *, *::before, *::after {
      box-sizing: border-box;
      -webkit-font-smoothing: antialiased;
    }

    html, body {
      margin: 0;
      padding: 0;
      width: 100%;
      height: 100%;
      overflow: hidden;
      font-family: var(--ow-font-sans);
      font-size: 12px;
      color: var(--ow-text-primary);
      background-color: var(--ow-bg-base);
      user-select: none;
    }

    button {
      font: inherit;
      color: inherit;
      background: transparent;
      border: 0;
      padding: 0;
      cursor: pointer;
      outline: none;
    }

    input {
      font: inherit;
      color: inherit;
      outline: none;
    }

    .ow-mono {
      font-family: var(--ow-font-mono);
      font-variant-numeric: tabular-nums;
    }

    .truncate {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    /* ======================================================================
       #omniwave-app — CHROMA CARD SHELL (ZERO-BLUR)
       ====================================================================== */
    #omniwave-app {
      position: relative;
      width: 100%;
      height: 100%;
      display: flex;
      flex-direction: column;
      overflow: hidden;
      color: var(--ow-text-primary);
      background-color: var(--ow-bg-base);
      background-image:
        radial-gradient(
          ellipse 92% 62% at 10% -4%,
          rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), calc(0.26 * var(--ambient-strength))) 0%,
          rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), calc(0.09 * var(--ambient-strength))) 44%,
          rgba(17, 18, 22, 0) 76%
        ),
        radial-gradient(
          ellipse 56% 36% at 92% 4%,
          rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), calc(0.08 * var(--ambient-strength))) 0%,
          rgba(17, 18, 22, 0) 68%
        );
      border: 1px solid rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), calc(0.28 * var(--ambient-strength)));
      box-shadow:
        inset 0 1px 0 rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), calc(0.22 * var(--ambient-strength))),
        inset 0 0 0 1px rgba(255, 255, 255, 0.03);
      transition:
        --chroma-r var(--ow-t-chroma),
        --chroma-g var(--ow-t-chroma),
        --chroma-b var(--ow-t-chroma),
        --ambient-strength 240ms ease,
        border-color var(--ow-t-chroma),
        box-shadow var(--ow-t-chroma);
    }

    #omniwave-app[data-mode="expanded"] {
      border-radius: var(--ow-radius-window);
    }

    #omniwave-app[data-mode="compact"] {
      border-radius: var(--ow-radius-pill);
      justify-content: center;
    }

    #omniwave-app.ow-suspended * {
      animation-play-state: paused !important;
      transition: none !important;
    }

    .ow-drag-region {
      cursor: grab;
      touch-action: none;
    }

    /* Frameless Titlebar (36px) */
    .ow-titlebar {
      height: var(--ow-titlebar-height);
      min-height: var(--ow-titlebar-height);
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0 12px;
      border-bottom: 1px solid var(--ow-hairline-soft);
      flex-shrink: 0;
    }

    .ow-brand-group {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }

    .ow-brand-title {
      font-size: 12.5px;
      font-weight: 600;
      letter-spacing: -0.01em;
      color: #FFFFFF;
    }

    .ow-bridge-dot {
      width: 8px;
      height: 8px;
      border-radius: 999px;
      flex-shrink: 0;
    }

    .ow-titlebar-actions {
      display: flex;
      align-items: center;
      gap: 2px;
      flex-shrink: 0;
    }

    .ow-icon-btn {
      width: 24px;
      height: 24px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      border-radius: var(--ow-radius-control);
      color: var(--ow-text-secondary);
      transition: background-color var(--ow-t-micro), color var(--ow-t-micro), transform var(--ow-t-micro);
      flex-shrink: 0;
    }
    .ow-icon-btn:hover {
      background-color: rgba(255, 255, 255, 0.08);
      color: var(--ow-text-primary);
    }
    .ow-icon-btn:active {
      transform: scale(0.95);
    }
    .ow-icon-btn.is-active {
      color: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
      background-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.14);
    }
    .ow-icon-btn.is-liked {
      color: #FB7185;
    }
    .ow-icon-btn.is-downloaded {
      color: #4ADE80;
    }
    .ow-icon-btn.is-close:hover {
      background-color: rgba(244, 63, 94, 0.22);
      color: #FDA4AF;
    }
    .ow-icon-btn.is-danger:hover {
      color: #FB7185;
    }
    .ow-icon-btn.sz-26 { width: 26px; height: 26px; }
    .ow-icon-btn.sz-28 { width: 28px; height: 28px; }
    .ow-icon-btn.sz-32 { width: 32px; height: 32px; }
    .ow-icon-btn.is-disabled { opacity: 0.26; pointer-events: none; }

    /* Hero Player */
    .ow-hero {
      flex-shrink: 0;
      padding: 12px 16px 10px;
    }

    .ow-hero-top {
      display: flex;
      align-items: center;
      gap: 12px;
    }

    .ow-cover-frame {
      position: relative;
      border-radius: var(--ow-radius-art);
      border: 1px solid rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.35);
      box-shadow: 0 8px 20px rgba(0, 0, 0, 0.36);
      overflow: hidden;
      flex-shrink: 0;
      background-color: var(--ow-bg-elevated);
      display: flex;
      align-items: center;
      justify-content: center;
      transition: border-color var(--ow-t-chroma);
    }
    .ow-cover-frame.hero { width: 64px; height: 64px; }
    .ow-cover-frame.compact { width: 42px; height: 42px; border-radius: 8px; }
    .ow-cover-frame img {
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
    }

    .ow-cover-placeholder {
      width: 100%;
      height: 100%;
      display: flex;
      align-items: center;
      justify-content: center;
      background: linear-gradient(135deg, rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.22), #15171E 75%);
      color: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
    }

    .ow-hero-meta {
      min-width: 0;
      flex: 1;
    }

    .ow-hero-title-row {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 8px;
    }

    .ow-hero-title {
      font-size: 14.5px;
      font-weight: 600;
      letter-spacing: -0.015em;
      line-height: 1.25;
      color: #FFFFFF;
    }

    .ow-hero-artist {
      font-size: 12px;
      color: var(--ow-text-secondary);
      margin-top: 2px;
    }

    .ow-source-pill-row {
      margin-top: 7px;
      display: flex;
      align-items: center;
      gap: 6px;
    }

    .ow-service-pill {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 9px;
      border-radius: 999px;
      border: 1px solid rgba(255, 255, 255, 0.08);
      background: rgba(24, 26, 32, 0.92);
      font-size: 10.5px;
      font-weight: 500;
      color: #E5E7EB;
    }

    .ow-dot {
      width: 6px;
      height: 6px;
      border-radius: 999px;
      flex-shrink: 0;
    }
    .ow-dot-lg {
      width: 8px;
      height: 8px;
      border-radius: 999px;
      flex-shrink: 0;
    }

    /* Progress Bar (GPU-composited scaleX, no layout thrashing) */
    .ow-prog-wrap {
      margin-top: 10px;
    }

    .ow-scrub-zone {
      position: relative;
      padding: 6px 0;
      cursor: pointer;
      touch-action: none;
    }

    .ow-scrub-track {
      width: 100%;
      height: 4px;
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.1);
      overflow: hidden;
    }

    .ow-scrub-fill {
      width: 100%;
      height: 100%;
      border-radius: 999px;
      background-color: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
      transform-origin: left center;
      transform: scaleX(0);
      will-change: transform;
      transition: background-color var(--ow-t-chroma);
    }

    .ow-scrub-tooltip {
      position: absolute;
      bottom: calc(100% + 2px);
      transform: translateX(-50%);
      padding: 2px 6px;
      border-radius: 5px;
      background: #0B0C10;
      border: 1px solid var(--ow-hairline);
      color: var(--ow-text-primary);
      font-family: var(--ow-font-mono);
      font-size: 10px;
      line-height: 1.2;
      pointer-events: none;
      opacity: 0;
      transition: opacity 120ms ease;
      white-space: nowrap;
      z-index: 20;
    }
    .ow-scrub-zone:hover .ow-scrub-tooltip,
    .ow-scrub-zone.is-scrubbing .ow-scrub-tooltip,
    .ow-pill-prog-zone:hover .ow-scrub-tooltip,
    .ow-pill-prog-zone.is-scrubbing .ow-scrub-tooltip {
      opacity: 1;
    }

    .ow-times-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      font-size: 10.5px;
      color: var(--ow-text-secondary);
      margin-top: 1px;
    }
    .ow-time-cur { color: #E5E7EB; }

    /* Controls & Volume Row */
    .ow-controls-row {
      margin-top: 8px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }

    .ow-transport-group {
      display: flex;
      align-items: center;
      gap: 3px;
    }

    .ow-play-btn {
      width: 40px;
      height: 40px;
      margin: 0 4px;
      border-radius: 999px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      background-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.92);
      color: #0B0D11;
      box-shadow:
        0 0 0 4px rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.18),
        0 6px 16px rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.28);
      transition:
        transform var(--ow-t-micro),
        background-color var(--ow-t-chroma),
        box-shadow var(--ow-t-chroma);
      flex-shrink: 0;
    }
    .ow-play-btn.compact {
      width: 32px;
      height: 32px;
      margin: 0 2px;
      box-shadow:
        0 0 0 3px rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.18),
        0 4px 12px rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.25);
    }
    .ow-play-btn:hover {
      background-color: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
      transform: scale(1.04);
    }
    .ow-play-btn:active {
      transform: scale(0.96);
    }

    .ow-vol-wrap {
      position: relative;
      display: flex;
      align-items: center;
      gap: 5px;
    }
    .ow-vol-tip {
      position: absolute;
      bottom: calc(100% + 4px);
      right: 0;
      padding: 2px 5px;
      border-radius: 5px;
      background: #0B0C10;
      border: 1px solid var(--ow-hairline);
      font-family: var(--ow-font-mono);
      font-size: 9.5px;
      color: var(--ow-text-primary);
      opacity: 0;
      pointer-events: none;
      transition: opacity 120ms ease;
    }
    .ow-vol-wrap:hover .ow-vol-tip,
    .ow-vol-wrap:focus-within .ow-vol-tip {
      opacity: 1;
    }

    .ow-slider {
      -webkit-appearance: none;
      appearance: none;
      background: transparent;
      cursor: pointer;
      height: 16px;
    }
    .ow-slider.w-vol { width: 62px; }
    .ow-slider.w-full { width: 100%; }
    .ow-slider::-webkit-slider-runnable-track {
      height: 3px;
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.14);
    }
    .ow-slider::-webkit-slider-thumb {
      -webkit-appearance: none;
      appearance: none;
      width: 10px;
      height: 10px;
      border-radius: 999px;
      background: #F3F4F6;
      margin-top: -3.5px;
      transition: transform 120ms ease, background-color var(--ow-t-chroma);
    }
    .ow-slider:hover::-webkit-slider-thumb {
      transform: scale(1.2);
      background: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
    }

    /* Segmented Tabs */
    .ow-tabs-bar {
      flex-shrink: 0;
      border-top: 1px solid var(--ow-hairline-soft);
      padding: 9px 16px 8px;
    }

    .ow-tabs-grid {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 4px;
      padding: 2px;
      border-radius: 10px;
      border: 1px solid var(--ow-hairline);
      background: var(--ow-bg-input);
    }

    .ow-tab-btn {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 4px;
      padding: 6px 4px;
      border-radius: 7px;
      font-size: 11px;
      font-weight: 500;
      color: var(--ow-text-secondary);
      transition: background-color var(--ow-t-micro), color var(--ow-t-micro);
    }
    .ow-tab-btn:hover { color: #E5E7EB; }
    .ow-tab-btn.is-active {
      background: var(--ow-bg-hover);
      color: #FFFFFF;
    }
    .ow-tab-badge {
      font-size: 9.5px;
      color: var(--ow-text-muted);
    }
    .ow-tab-btn.is-active .ow-tab-badge {
      color: #D1D5DB;
    }

    /* Tab Content Area */
    .ow-tab-panel {
      flex: 1;
      min-height: 0;
      display: flex;
      flex-direction: column;
      padding: 0 16px 12px;
    }

    .ow-scroll {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding-right: 4px;
      scrollbar-width: thin;
      scrollbar-color: rgba(156, 163, 175, 0.24) transparent;
    }
    .ow-scroll::-webkit-scrollbar { width: 4px; }
    .ow-scroll::-webkit-scrollbar-track { background: transparent; }
    .ow-scroll::-webkit-scrollbar-thumb {
      background: rgba(156, 163, 175, 0.24);
      border-radius: 999px;
    }
    .ow-scroll::-webkit-scrollbar-thumb:hover {
      background: rgba(156, 163, 175, 0.44);
    }

    /* Search & Filter Controls */
    .ow-search-header {
      flex-shrink: 0;
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin-bottom: 8px;
    }

    .ow-search-box {
      position: relative;
      display: flex;
      align-items: center;
    }
    .ow-search-icon {
      position: absolute;
      left: 10px;
      color: var(--ow-text-muted);
      pointer-events: none;
      display: flex;
    }
    .ow-search-input {
      width: 100%;
      border-radius: var(--ow-radius-control);
      border: 1px solid var(--ow-hairline);
      background: var(--ow-bg-input);
      padding: 6px 28px 6px 32px;
      font-size: 12px;
      color: #FFFFFF;
      transition: border-color var(--ow-t-micro);
    }
    .ow-search-input::placeholder { color: var(--ow-text-muted); }
    .ow-search-input:focus { border-color: rgba(255, 255, 255, 0.25); }

    .ow-kbd-hint {
      position: absolute;
      right: 8px;
      border-radius: 4px;
      border: 1px solid #2A2E39;
      background: var(--ow-bg-elevated);
      padding: 1px 6px;
      font-size: 9.5px;
      color: var(--ow-text-muted);
      pointer-events: none;
    }
    .ow-clear-input-btn {
      position: absolute;
      right: 6px;
      width: 20px;
      height: 20px;
      border-radius: 5px;
      display: flex;
      align-items: center;
      justify-content: center;
      color: var(--ow-text-secondary);
    }
    .ow-clear-input-btn:hover { background: rgba(255, 255, 255, 0.1); color: #FFFFFF; }

    .ow-chips-scroller {
      position: relative;
      display: flex;
      align-items: center;
      width: 100%;
      min-width: 0;
    }

    .ow-chips-row {
      display: flex;
      align-items: center;
      gap: 4px;
      flex: 1;
      min-width: 0;
      overflow-x: auto;
      scrollbar-width: none;
      scroll-behavior: smooth;
      user-select: none;
      padding: 0 2px 2px;
      flex-shrink: 0;
    }
    .ow-chips-row.is-scrollable { cursor: grab; }
    .ow-chips-row.is-dragging { cursor: grabbing; scroll-behavior: auto; }
    .ow-chips-row::-webkit-scrollbar { display: none; }

    .ow-chips-nav {
      position: absolute;
      top: 0;
      bottom: 2px;
      width: 24px;
      display: flex;
      align-items: center;
      color: #E5E7EB;
      font-size: 14px;
      font-weight: 600;
      cursor: pointer;
      z-index: 2;
      transition: opacity 120ms ease, color 120ms ease;
    }
    .ow-chips-nav.is-left {
      left: 0;
      justify-content: flex-start;
      padding-left: 2px;
      background: linear-gradient(90deg, var(--ow-bg-base) 62%, rgba(17, 18, 22, 0));
    }
    .ow-chips-nav.is-right {
      right: 0;
      justify-content: flex-end;
      padding-right: 2px;
      background: linear-gradient(270deg, var(--ow-bg-base) 62%, rgba(17, 18, 22, 0));
    }
    .ow-chips-nav:hover { color: #FFFFFF; }
    .ow-chips-nav.is-hidden { opacity: 0; pointer-events: none; }

    .ow-chip {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      padding: 4px 9px;
      border-radius: 999px;
      background: var(--ow-bg-elevated);
      color: var(--ow-text-secondary);
      font-size: 10.5px;
      font-weight: 500;
      white-space: nowrap;
      flex-shrink: 0;
      transition: background-color var(--ow-t-micro), color var(--ow-t-micro), border-color var(--ow-t-micro);
    }
    .ow-chip:hover { color: #E5E7EB; }
    .ow-chip.is-active {
      background: rgba(255, 255, 255, 0.15);
      color: #FFFFFF;
    }
    .ow-chip.is-action {
      border: 1px solid var(--ow-hairline);
      background: #14161C;
    }
    .ow-chip.is-action:hover {
      border-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.45);
      color: #FFFFFF;
    }

    .ow-subbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 6px;
      font-size: 10.5px;
      color: var(--ow-text-muted);
    }

    /* Track Rows */
    .ow-track-list {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .ow-track-row {
      display: flex;
      align-items: center;
      gap: 9px;
      border-radius: 9px;
      padding: 5px 8px;
      cursor: pointer;
      transition: background-color var(--ow-t-micro);
    }
    .ow-track-row:hover { background: var(--ow-bg-hover); }
    .ow-track-row.is-current { background: rgba(255, 255, 255, 0.08); }

    .ow-row-thumb {
      position: relative;
      width: 34px;
      height: 34px;
      border-radius: 7px;
      overflow: hidden;
      flex-shrink: 0;
      background: var(--ow-bg-elevated);
      display: flex;
      align-items: center;
      justify-content: center;
      color: var(--ow-text-muted);
    }
    .ow-row-thumb img {
      width: 100%;
      height: 100%;
      object-fit: cover;
      display: block;
    }
    .ow-row-eq-overlay {
      position: absolute;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      background: rgba(0, 0, 0, 0.58);
    }

    .ow-row-meta {
      min-width: 0;
      flex: 1;
    }
    .ow-row-title {
      font-size: 12px;
      font-weight: 500;
      line-height: 1.25;
      color: #E5E7EB;
    }
    .ow-track-row.is-current .ow-row-title { color: #FFFFFF; }
    .ow-row-sub {
      margin-top: 2px;
      display: flex;
      align-items: center;
      gap: 5px;
      font-size: 11px;
      color: var(--ow-text-secondary);
    }

    .ow-row-dur {
      font-size: 10.5px;
      color: var(--ow-text-muted);
      flex-shrink: 0;
    }
    .ow-row-actions {
      display: none;
      align-items: center;
      gap: 2px;
      flex-shrink: 0;
    }
    .ow-track-row:hover .ow-row-dur,
    .ow-track-row:hover .ow-row-fav-static {
      display: none;
    }
    .ow-track-row:hover .ow-row-actions {
      display: flex;
    }

    /* Queue Controls Row */
    .ow-q-actions {
      display: flex;
      align-items: center;
      gap: 2px;
      flex-shrink: 0;
    }

    /* Animated 3-bar Equalizer (pure transform scaleY) */
    @keyframes owEq {
      0%, 100% { transform: scaleY(0.28); }
      50% { transform: scaleY(1); }
    }
    .ow-eq {
      display: inline-flex;
      align-items: flex-end;
      gap: 2px;
      height: 11px;
      width: 11px;
      color: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
    }
    .ow-eq span {
      display: block;
      width: 2px;
      height: 100%;
      background: currentColor;
      border-radius: 1px;
      transform-origin: bottom;
      animation: owEq 0.78s ease-in-out infinite;
    }
    .ow-eq span:nth-child(2) { animation-delay: 0.2s; }
    .ow-eq span:nth-child(3) { animation-delay: 0.4s; }
    .ow-eq.paused span {
      animation: none;
      transform: scaleY(0.3);
    }

    /* Cards & Settings Sections */
    .ow-stack {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding-bottom: 4px;
    }
    .ow-card-block {
      border-radius: 10px;
      border: 1px solid var(--ow-hairline);
      background: var(--ow-bg-elevated);
      padding: 11px 12px;
      display: flex;
      flex-direction: column;
      gap: 9px;
    }
    .ow-now-card {
      display: flex;
      align-items: center;
      gap: 10px;
      border-radius: 10px;
      border: 1px solid rgba(255, 255, 255, 0.08);
      background: var(--ow-bg-elevated);
      padding: 8px 10px;
    }
    .ow-section-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      margin-bottom: 4px;
    }
    .ow-section-label {
      font-size: 10.5px;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--ow-text-secondary);
    }
    .ow-link-btn {
      font-size: 11px;
      color: var(--ow-text-secondary);
      transition: color var(--ow-t-micro);
    }
    .ow-link-btn:hover { color: #FB7185; }
    .ow-link-btn.accent:hover { color: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b)); }

    .ow-setting-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
    }
    .ow-bridge-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      border-radius: 8px;
      background: #13151B;
      padding: 6px 10px;
    }
    .ow-bridge-left {
      display: flex;
      align-items: center;
      gap: 8px;
      min-width: 0;
    }
    .ow-bridge-sub {
      font-size: 10px;
      color: var(--ow-text-muted);
    }

    .ow-badge-btn {
      border-radius: 6px;
      border: 1px solid rgba(255, 255, 255, 0.1);
      background: rgba(255, 255, 255, 0.05);
      padding: 3px 8px;
      font-size: 10.5px;
      font-weight: 500;
      color: var(--ow-text-secondary);
      flex-shrink: 0;
      transition: background-color var(--ow-t-micro), color var(--ow-t-micro);
    }
    .ow-badge-btn:hover { color: #FFFFFF; background: rgba(255, 255, 255, 0.1); }
    .ow-badge-btn.is-ok {
      color: #4ADE80;
      background: rgba(34, 197, 94, 0.1);
      border-color: rgba(34, 197, 94, 0.25);
    }
    .ow-badge-btn.is-warn {
      color: #FBBF24;
      background: rgba(245, 158, 11, 0.1);
      border-color: rgba(245, 158, 11, 0.25);
    }

    .ow-btn-secondary {
      width: 100%;
      border-radius: 8px;
      border: 1px solid var(--ow-hairline);
      background: #13151B;
      padding: 6px 10px;
      font-size: 11px;
      font-weight: 500;
      color: #D1D5DB;
      text-align: center;
      transition: background-color var(--ow-t-micro), color var(--ow-t-micro), border-color var(--ow-t-micro);
    }
    .ow-btn-secondary:hover {
      background: var(--ow-bg-hover);
      color: #FFFFFF;
      border-color: rgba(255, 255, 255, 0.18);
    }

    .ow-btn-grid {
      display: grid;
      grid-template-columns: repeat(2, 1fr);
      gap: 6px;
    }

    /* Switch */
    .ow-switch {
      position: relative;
      width: 34px;
      height: 18px;
      border-radius: 999px;
      background: #272A34;
      border: 1px solid #323643;
      transition: background-color 150ms ease, border-color 150ms ease;
      flex-shrink: 0;
    }
    .ow-switch::after {
      content: "";
      position: absolute;
      top: 2px;
      left: 2px;
      width: 12px;
      height: 12px;
      border-radius: 999px;
      background: #D1D5DB;
      transition: transform 150ms ease, background-color 150ms ease;
    }
    .ow-switch[aria-checked="true"] {
      background: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.85);
      border-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 1);
    }
    .ow-switch[aria-checked="true"]::after {
      transform: translateX(16px);
      background: #0B0D11;
    }

    /* Shortcuts & Telemetry Card */
    .ow-kbd-legend {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 6px;
      font-size: 10.5px;
      color: var(--ow-text-secondary);
    }
    .ow-kbd-item {
      display: inline-flex;
      align-items: center;
      gap: 4px;
    }
    .ow-kbd-key {
      border-radius: 4px;
      border: 1px solid #2D313D;
      background: var(--ow-bg-base);
      padding: 1px 5px;
      font-family: var(--ow-font-mono);
      font-size: 9.5px;
      color: #E5E7EB;
    }
    .ow-telemetry-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      border-top: 1px solid var(--ow-hairline);
      padding-top: 8px;
      font-family: var(--ow-font-mono);
      font-size: 10px;
      color: var(--ow-text-secondary);
    }
    .ow-telemetry-left {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }

    /* Playlists & Favorites Segmented Subbar */
    .ow-seg-group {
      display: grid;
      grid-template-columns: repeat(2, 1fr);
      gap: 2px;
      padding: 2px;
      border-radius: 8px;
      background: var(--ow-bg-input);
      border: 1px solid var(--ow-hairline);
    }
    .ow-seg-btn {
      padding: 4px 8px;
      border-radius: 6px;
      font-size: 10.5px;
      font-weight: 500;
      color: var(--ow-text-secondary);
      text-align: center;
      transition: background-color var(--ow-t-micro), color var(--ow-t-micro);
    }
    .ow-seg-btn.is-active {
      background: var(--ow-bg-hover);
      color: #FFFFFF;
    }

    .ow-pl-create {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .ow-pl-input {
      flex: 1;
      border-radius: 8px;
      border: 1px solid var(--ow-hairline);
      background: var(--ow-bg-input);
      padding: 5px 10px;
      font-size: 11.5px;
      color: #FFFFFF;
    }
    .ow-pl-input::placeholder { color: var(--ow-text-muted); }

    .ow-pl-card {
      display: flex;
      align-items: center;
      gap: 10px;
      border-radius: 9px;
      border: 1px solid var(--ow-hairline);
      background: var(--ow-bg-elevated);
      padding: 7px 9px;
      cursor: pointer;
      transition: background-color var(--ow-t-micro), border-color var(--ow-t-micro);
    }
    .ow-pl-card:hover {
      background: var(--ow-bg-hover);
      border-color: rgba(255, 255, 255, 0.14);
    }

    /* Compact Chroma Pill (324px × 66px) */
    .ow-compact-pill {
      position: relative;
      width: 100%;
      height: 100%;
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 0 10px;
    }
    .ow-compact-meta {
      min-width: 0;
      flex: 1;
      padding-right: 4px;
    }
    .ow-compact-title {
      font-size: 12.5px;
      font-weight: 600;
      line-height: 1.25;
      color: #FFFFFF;
    }
    .ow-compact-sub {
      margin-top: 2px;
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 11px;
      color: var(--ow-text-secondary);
    }
    .ow-compact-actions {
      display: flex;
      align-items: center;
      gap: 2px;
      flex-shrink: 0;
    }
    .ow-pill-prog-zone {
      position: absolute;
      left: 0;
      right: 0;
      bottom: 0;
      height: 6px;
      display: flex;
      align-items: flex-end;
      cursor: pointer;
      touch-action: none;
    }
    .ow-pill-prog-track {
      width: 100%;
      height: 2px;
      background: rgba(255, 255, 255, 0.08);
      overflow: hidden;
    }

    .ow-empty {
      padding: 26px 10px;
      text-align: center;
      font-size: 11.5px;
      color: var(--ow-text-muted);
      line-height: 1.5;
    }

    .ow-empty-dashed {
      border-radius: 10px;
      border: 1px dashed var(--ow-hairline);
      padding: 22px 12px;
      text-align: center;
      font-size: 11.5px;
      color: var(--ow-text-muted);
    }

    /* Toast status */
    .ow-toast {
      position: fixed;
      left: 50%;
      bottom: 12px;
      transform: translate(-50%, 8px);
      padding: 5px 10px;
      border-radius: 8px;
      border: 1px solid #2A2E39;
      background: #14161C;
      color: #F3F4F6;
      font-size: 11px;
      opacity: 0;
      pointer-events: none;
      transition: opacity 150ms ease, transform 150ms ease;
      z-index: 40;
      max-width: 90%;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .ow-toast.is-visible {
      opacity: 1;
      transform: translate(-50%, 0);
    }

    /* Variant selector pills on grouped search tracks */
    .ow-variant-pills {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin-top: 4px;
    }
    .ow-var-chip {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 1px 6px;
      border-radius: 999px;
      border: 1px solid rgba(255, 255, 255, 0.08);
      background: rgba(13, 14, 18, 0.85);
      font-size: 9.5px;
      font-weight: 500;
      color: var(--ow-text-secondary);
      line-height: 1.4;
      transition: background-color var(--ow-t-micro), border-color var(--ow-t-micro), color var(--ow-t-micro);
    }
    .ow-var-chip:hover {
      background: rgba(255, 255, 255, 0.08);
      color: #FFFFFF;
    }
    .ow-var-chip.is-selected {
      background: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.16);
      border-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.55);
      color: #FFFFFF;
    }

    /* Updated 2-row Compact Chroma Pill (348x84) + Edge Auto-Hide */
    .ow-compact-pill {
      position: relative;
      width: 100%;
      height: 100%;
      display: flex;
      flex-direction: column;
      justify-content: center;
      gap: 4px;
      padding: 6px 10px 8px;
      transition: opacity 150ms ease;
    }
    .ow-compact-top {
      display: flex;
      align-items: center;
      gap: 8px;
      width: 100%;
      min-width: 0;
    }
    .ow-cover-frame.compact { width: 38px; height: 38px; border-radius: 8px; }
    .ow-compact-vol-row {
      display: flex;
      align-items: center;
      gap: 6px;
      width: 100%;
    }
    .ow-compact-vol-slider {
      flex: 1;
      min-width: 60px;
      height: 14px;
    }
    .ow-compact-pct {
      font-size: 9.5px;
      color: var(--ow-text-secondary);
      width: 28px;
      text-align: right;
      flex-shrink: 0;
    }
    .ow-compact-time {
      font-size: 9.5px;
      color: var(--ow-text-muted);
      flex-shrink: 0;
      margin-left: 2px;
    }

    #omniwave-app[data-mode="compact"][data-edge-collapsed="true"] .ow-compact-pill {
      opacity: 0;
      pointer-events: none;
    }
    #omniwave-app[data-mode="compact"][data-edge-collapsed="true"]::after {
      content: "";
      position: absolute;
      border-radius: 999px;
      pointer-events: none;
      background: linear-gradient(180deg, rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.35), rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b)), rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.35));
    }
    #omniwave-app[data-mode="compact"][data-edge="left"][data-edge-collapsed="true"]::after {
      top: 14px;
      bottom: 14px;
      right: 2px;
      width: 4px;
    }
    #omniwave-app[data-mode="compact"][data-edge="right"][data-edge-collapsed="true"]::after {
      top: 14px;
      bottom: 14px;
      left: 2px;
      width: 4px;
    }
    #omniwave-app[data-mode="compact"][data-edge="top"][data-edge-collapsed="true"]::after {
      left: 26px;
      right: 26px;
      bottom: 2px;
      height: 4px;
      background: linear-gradient(90deg, rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.35), rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b)), rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.35));
    }

    /* Drag-and-Drop indicators & Playlist pins */
    .ow-track-row.is-dragging,
    .ow-pl-card.is-dragging {
      opacity: 0.42;
    }
    .ow-track-row.is-drag-over-top,
    .ow-pl-card.is-drag-over-top {
      box-shadow: inset 0 2px 0 rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
    }
    .ow-track-row.is-drag-over-bottom,
    .ow-pl-card.is-drag-over-bottom {
      box-shadow: inset 0 -2px 0 rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
    }
    .ow-pl-card.is-pinned {
      border-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.35);
    }
    .ow-icon-btn.is-yandex:hover {
      color: #FFCC00;
      background-color: rgba(255, 204, 0, 0.14);
    }

    /* Unified Settings inputs & inline browser auth */
    .ow-input {
      width: 100%;
      border-radius: 7px;
      border: 1px solid var(--ow-hairline);
      background: var(--ow-bg-input);
      padding: 5px 8px;
      font-size: 11px;
      color: #FFFFFF;
      transition: border-color var(--ow-t-micro);
    }
    .ow-input::placeholder { color: var(--ow-text-muted); }
    .ow-input:focus { border-color: rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.65); }
    .ow-input-row {
      display: flex;
      align-items: center;
      gap: 6px;
    }
    .ow-field-label {
      font-size: 10px;
      color: var(--ow-text-muted);
      margin-bottom: 3px;
    }
    .ow-grid-2 {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 6px;
    }
    .ow-grid-4 {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 4px;
    }
    .ow-inline-auth {
      margin-top: 6px;
      border-radius: 8px;
      border: 1px solid rgba(var(--chroma-r), var(--chroma-g), var(--chroma-b), 0.28);
      background: #0F1117;
      padding: 8px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .ow-status-line {
      font-size: 10.5px;
      line-height: 1.35;
      color: var(--ow-text-secondary);
    }
    .ow-status-line.is-ok { color: #4ADE80; }
    .ow-status-line.is-warn { color: #FBBF24; }
    .ow-status-line.is-err { color: #FB7185; }
    .ow-progress-bar {
      width: 100%;
      height: 4px;
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.1);
      overflow: hidden;
    }
    .ow-progress-fill {
      height: 100%;
      border-radius: 999px;
      background: rgb(var(--chroma-r), var(--chroma-g), var(--chroma-b));
      transition: width 150ms ease;
    }
  </style>
</head>
<body>
  <section id="omniwave-app" data-mode="expanded" aria-label="OmniWave Desktop Player"></section>
  <div id="ow-toast" class="ow-toast"></div>

  <script>
    (function () {
      'use strict';

      const appRoot = document.getElementById('omniwave-app');
      const toastEl = document.getElementById('ow-toast');
      let toastTimer = null;
      let searchDebounceTimer = null;
      let chromaDebounceTimer = null;

      function sendToHost(type, payload) {
        try {
          if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage(JSON.stringify(Object.assign({ type: type }, payload || {})));
          }
        } catch (e) {}
      }

      const SOURCES = {
        all:          { id: 'all',          badge: 'Все',        fullName: 'Все сервисы',   color: '#9CA3AF', rgb: [255, 204, 0] },
        yandex:       { id: 'yandex',       badge: 'Я.Музыка',   fullName: 'Яндекс Музыка', color: '#FFCC00', rgb: [255, 204, 0] },
        vk:           { id: 'vk',           badge: 'VK',         fullName: 'VK Музыка',     color: '#2688EB', rgb: [38, 136, 235] },
        spotify:      { id: 'spotify',      badge: 'Spotify',    fullName: 'Spotify',       color: '#1DB954', rgb: [29, 185, 84] },
        soundcloud:   { id: 'soundcloud',   badge: 'SC',         fullName: 'SoundCloud',    color: '#FF5500', rgb: [255, 85, 0] },
        youtube:      { id: 'youtube',      badge: 'YT',         fullName: 'YouTube',       color: '#FF3B30', rgb: [255, 59, 48] },
        audius:       { id: 'audius',       badge: 'Audius',     fullName: 'Audius',        color: '#CC0FF0', rgb: [204, 15, 240] },
        bandcamp:     { id: 'bandcamp',     badge: 'Bandcamp',   fullName: 'Bandcamp',      color: '#1DA0C3', rgb: [29, 160, 195] },
        open_catalog: { id: 'open_catalog', badge: 'Archive',    fullName: 'Каталог',       color: '#22C55E', rgb: [34, 197, 94] },
        radio:        { id: 'radio',        badge: 'Радио',      fullName: 'Радио',         color: '#F59E0B', rgb: [245, 158, 11] },
        web:          { id: 'web',          badge: 'URL',        fullName: 'Прямой поток',  color: '#22C55E', rgb: [34, 197, 94] }
      };

      const FILTER_CHIPS = ['all', 'yandex', 'vk', 'spotify', 'soundcloud', 'youtube', 'radio'];

      const ICONS = {
        wave: '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h3l2.5-6 3 12 3-9 2 3H21"/></svg>',
        pin: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m15 4 5 5-2 2-1.5-1.5-4.5 4.5V17l-2 2-3-3-4 4 4-4-3-3 2-2h3l4.5-4.5L13 6l2-2Z"/></svg>',
        compact: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="3"/><path d="M12 12h6v5h-6z"/></svg>',
        expand: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></svg>',
        minimize: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M5 12h14"/></svg>',
        close: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round"><path d="M18 6 6 18M6 6l12 12"/></svg>',
        play: '<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor" style="transform:translateX(1px)"><path d="M7.5 5.25a.75.75 0 0 1 1.13-.65l10.5 6.75a.75.75 0 0 1 0 1.3l-10.5 6.75a.75.75 0 0 1-1.13-.65V5.25Z"/></svg>',
        pause: '<svg width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><rect x="6.5" y="5" width="3.8" height="14" rx="1"/><rect x="13.7" y="5" width="3.8" height="14" rx="1"/></svg>',
        prev: '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="5" y="5.5" width="2.2" height="13" rx="0.6"/><path d="M18.2 6.2a.75.75 0 0 0-1.15-.63l-8.4 5.8a.75.75 0 0 0 0 1.24l8.4 5.8a.75.75 0 0 0 1.15-.62V6.2Z"/></svg>',
        next: '<svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="16.8" y="5.5" width="2.2" height="13" rx="0.6"/><path d="M5.8 6.2a.75.75 0 0 1 1.15-.63l8.4 5.8a.75.75 0 0 1 0 1.24l-8.4 5.8a.75.75 0 0 1-1.15-.62V6.2Z"/></svg>',
        shuffle: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m18 14 3 3-3 3"/><path d="m18 4 3 3-3 3"/><path d="M3 17h4l10-10h4"/><path d="M3 7h4l3 3"/><path d="m14 14 3 3h4"/></svg>',
        repeat: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m17 2 4 4-4 4"/><path d="M3 11v-1a4 4 0 0 1 4-4h14"/><path d="m7 22-4-4 4-4"/><path d="M21 13v1a4 4 0 0 1-4 4H3"/></svg>',
        repeatOne: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m17 2 4 4-4 4"/><path d="M3 11v-1a4 4 0 0 1 4-4h14"/><path d="m7 22-4-4 4-4"/><path d="M21 13v1a4 4 0 0 1-4 4H3"/><path d="M11 10h1v4"/></svg>',
        heartOff: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M19.5 12.57 12 20l-7.5-7.43A5 5 0 1 1 12 6.01a5 5 0 1 1 7.5 6.56Z"/></svg>',
        heartOn: '<svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor"><path d="M19.5 12.57 12 20l-7.5-7.43A5 5 0 1 1 12 6.01a5 5 0 1 1 7.5 6.56Z"/></svg>',
        volOn: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4V5Z"/><path d="M15.5 8.5a5 5 0 0 1 0 7"/><path d="M18.5 5.8a9 9 0 0 1 0 12.4"/></svg>',
        volLow: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4V5Z"/><path d="M15.5 8.5a5 5 0 0 1 0 7"/></svg>',
        volMute: '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 5 6 9H3v6h3l5 4V5Z"/><path d="m22 9-6 6"/><path d="m16 9 6 6"/></svg>',
        search: '<svg width="13.5" height="13.5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>',
        plus: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>',
        download: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12m0 0-4-4m4 4 4-4M4 19h16"/></svg>',
        check: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>',
        up: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m18 15-6-6-6 6"/></svg>',
        down: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m6 9 6 6 6-6"/></svg>',
        note: '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/></svg>',
        dockEdge: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 4v16"/><path d="M4 12h10"/><path d="m10 8 4 4-4 4"/></svg>',
        sparkle: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m12 3 1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9L12 3Z"/></svg>',
        eye: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z"/><circle cx="12" cy="12" r="3"/></svg>',
        eyeOff: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9.88 9.88a3 3 0 1 0 4.24 4.24"/><path d="M10.73 5.08A10.43 10.43 0 0 1 12 5c7 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68"/><path d="M6.61 6.61A13.53 13.53 0 0 0 2 12s3 7 10 7a9.74 9.74 0 0 0 5.39-1.61"/><path d="m2 2 20 20"/></svg>',
        folder: '<svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 20h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9l-2-2H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2Z"/></svg>',
        external: '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 3h7v7"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/></svg>'
      };

      const state = {
        compact: false,
        activeTab: 'search',
        favSubTab: 'tracks',
        openedPlaylistId: null,
        pendingFocusSearch: false,
        pendingFocusNewPlaylist: false,
        pendingScrollSection: null,
        currentTrack: null,
        coverDataUrl: null,
        isPlaying: false,
        currentMs: 0,
        totalMs: 0,
        volume: 80,
        shuffle: false,
        repeatMode: 0,
        searchQuery: '',
        searchFilter: 'all',
        chipsScroll: { search: 0, fav: 0 },
        searchStatus: '',
        searchLoading: false,
        canLoadMore: false,
        searchResults: [],
        queue: [],
        favorites: [],
        favFilter: 'all',
        playlists: [],
        accounts: {
          yandexConnected: false,
          yandexUserName: '',
          vkConnected: false,
          vkUserName: '',
          spotifyConnected: false,
          spotifyUserName: '',
          youtubeCookiesConnected: false,
          proxyActive: false
        },
        externalSource: null,
        windowSettings: {
          alwaysOnTop: true,
          minimizeToTray: true,
          compactEdgeAutoHide: true,
          dockedEdge: '',
          edgeCollapsed: false,
          startMinimized: false,
          autoPauseOnSwitch: true,
          autoStart: false,
          chromaIntensity: 67,
          windowOpacity: 100,
          zeroBlurMode: true,
          downloadFolder: ''
        },
        proxy: {
          enabled: false,
          type: 'http',
          host: '',
          port: 8080,
          username: '',
          password: '',
          bypassRu: true,
          text: 'Прокси выключен — используется системное подключение.',
          kind: 'off',
          testing: false
        },
        proxyDraft: {
          dirty: false,
          enabled: false,
          type: 'http',
          host: '',
          port: '8080',
          username: '',
          password: '',
          bypassRu: true
        },
        proxyPassVisible: false,
        authPanel: {
          provider: '',
          mode: '',
          userCode: '',
          verificationUrl: '',
          input: '',
          busy: false,
          statusText: '',
          statusOk: null
        },
        ytDlp: {
          text: 'Нажмите «Проверить»',
          ok: false,
          busy: false
        },
        update: {
          text: 'Нажмите «Проверить»',
          checkedOk: false,
          isAvailable: false,
          busy: false,
          progress: 0
        },
        eq: { enabled: false, preset: 'Flat', preamp: 0, bands: [], rate: 1.0, presets: {} },
        telemetry: { ramMb: 24, cpuPercent: 0.2, gpuPercent: 0 },
        suspended: false
      };

      /* ======================================================================
         CHROMA COLOR ENGINE (16x16 OFFSCREEN CANVAS + LRU MAP)
         ====================================================================== */
      const ChromaEngine = (() => {
        const canvas = document.createElement('canvas');
        canvas.width = 16;
        canvas.height = 16;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        const cache = new Map();
        const MAX_CACHE = 128;
        let lastKey = '';

        function fallbackRgb(brandKey) {
          const s = SOURCES[(brandKey || '').toLowerCase()];
          return s ? s.rgb : [255, 204, 0];
        }

        function remember(key, rgb) {
          if (!key) return;
          if (cache.has(key)) cache.delete(key);
          cache.set(key, rgb);
          if (cache.size > MAX_CACHE) {
            const oldest = cache.keys().next().value;
            if (oldest !== undefined) cache.delete(oldest);
          }
        }

        function applyRgb(rgb) {
          const root = document.documentElement.style;
          root.setProperty('--chroma-r', String(rgb[0]));
          root.setProperty('--chroma-g', String(rgb[1]));
          root.setProperty('--chroma-b', String(rgb[2]));
        }

        function syncStrength() {
          const ws = state.windowSettings;
          const strength = Math.max(0, Math.min(1.5, (ws.chromaIntensity ?? 67) / 67));
          document.documentElement.style.setProperty('--ambient-strength', strength.toFixed(3));
        }

        function extractFromImage(img, brandKey) {
          if (!ctx) return fallbackRgb(brandKey);
          try {
            ctx.clearRect(0, 0, 16, 16);
            ctx.drawImage(img, 0, 0, 16, 16);
            const data = ctx.getImageData(0, 0, 16, 16).data;
            let rSum = 0, gSum = 0, bSum = 0, wSum = 0;

            for (let i = 0; i < data.length; i += 4) {
              const a = data[i + 3];
              if (a < 128) continue;
              const r = data[i], g = data[i + 1], b = data[i + 2];
              const max = Math.max(r, g, b);
              const min = Math.min(r, g, b);
              if (max < 36 || (min > 230 && max - min < 14)) continue;
              const sat = max === 0 ? 0 : (max - min) / max;
              if (sat < 0.12) continue;
              const luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
              const w = sat * (0.45 + 0.55 * luma);
              rSum += r * w;
              gSum += g * w;
              bSum += b * w;
              wSum += w;
            }

            if (wSum <= 0.01) return fallbackRgb(brandKey);
            let rAvg = Math.round(rSum / wSum);
            let gAvg = Math.round(gSum / wSum);
            let bAvg = Math.round(bSum / wSum);

            const peak = Math.max(rAvg, gAvg, bAvg);
            if (peak > 0 && peak < 150) {
              const boost = 175 / peak;
              rAvg = Math.min(255, Math.round(rAvg * boost));
              gAvg = Math.min(255, Math.round(gAvg * boost));
              bAvg = Math.min(255, Math.round(bAvg * boost));
            }
            return [rAvg, gAvg, bAvg];
          } catch (e) {
            return fallbackRgb(brandKey);
          }
        }

        function updateForTrack(track, coverDataUrl) {
          syncStrength();
          if (!track) {
            applyRgb([255, 204, 0]);
            lastKey = '';
            return;
          }

          const brandKey = track.brandKey || 'yandex';
          const srcUrl = coverDataUrl || track.coverUrl || '';
          const cacheKey = srcUrl ? (srcUrl.length > 96 ? (track.uniqueKey + '|' + srcUrl.length) : srcUrl) : ('brand:' + brandKey);

          if (cacheKey === lastKey) return;
          lastKey = cacheKey;

          if (!srcUrl) {
            applyRgb(fallbackRgb(brandKey));
            return;
          }

          if (cache.has(cacheKey)) {
            const cached = cache.get(cacheKey);
            remember(cacheKey, cached);
            applyRgb(cached);
            return;
          }

          applyRgb(fallbackRgb(brandKey));
          const img = new Image();
          if (!coverDataUrl) img.crossOrigin = 'anonymous';
          img.onload = () => {
            if (lastKey !== cacheKey) return;
            const rgb = extractFromImage(img, brandKey);
            remember(cacheKey, rgb);
            applyRgb(rgb);
          };
          img.onerror = () => {
            if (lastKey !== cacheKey) return;
            const fb = fallbackRgb(brandKey);
            remember(cacheKey, fb);
            applyRgb(fb);
          };
          img.src = srcUrl;
        }

        return { updateForTrack, syncStrength };
      })();

      function esc(str) {
        return String(str || '')
          .replace(/&/g, '&amp;')
          .replace(/</g, '&lt;')
          .replace(/>/g, '&gt;')
          .replace(/"/g, '&quot;');
      }

      function fmtMs(ms) {
        const totalSec = Math.max(0, Math.floor((ms || 0) / 1000));
        const m = Math.floor(totalSec / 60);
        const s = totalSec % 60;
        return String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0');
      }

      function getVolIcon(vol) {
        if (vol <= 0) return ICONS.volMute;
        if (vol < 45) return ICONS.volLow;
        return ICONS.volOn;
      }

      function getSourceMeta(brandKey) {
        return SOURCES[(brandKey || '').toLowerCase()] || { id: 'default', badge: 'OmniWave', fullName: 'OmniWave', color: '#FFCC00', rgb: [255, 204, 0] };
      }

      function getBridgeMeta() {
        const acc = state.accounts;
        const count = [acc.yandexConnected, acc.vkConnected, acc.spotifyConnected].filter(Boolean).length;
        if (state.externalSource || count >= 2) {
          return { color: 'var(--status-ok)', label: state.externalSource ? 'Активна системная медиа-сессия' : ('Подключено аккаунтов: ' + count) };
        }
        if (count === 1) return { color: 'var(--status-warn)', label: 'Подключён 1 сервис · гостевой поиск активен' };
        return { color: 'var(--status-warn)', label: 'Гостевой режим · откройте Настройки для входа' };
      }

      function showToast(msg) {
        if (!msg) return;
        toastEl.textContent = msg;
        toastEl.classList.add('is-visible');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => toastEl.classList.remove('is-visible'), 1800);
      }

      function coverMarkup() {
        const track = state.currentTrack;
        const src = state.coverDataUrl || (track && track.coverUrl) || '';
        if (src) return '<img src="' + esc(src) + '" alt="" loading="eager" />';
        return '<div class="ow-cover-placeholder">' + ICONS.note + '</div>';
      }

      function eqClass() {
        return 'ow-eq' + (state.isPlaying && !state.suspended ? '' : ' paused');
      }

      function selectSearchVariant(groupKey, trackKey, autoPlay) {
        const idx = state.searchResults.findIndex(item => item && (item.groupKey || item.uniqueKey) === groupKey);
        if (idx < 0) return;
        const grp = state.searchResults[idx];
        if (!Array.isArray(grp.variants)) return;
        const vIdx = grp.variants.findIndex(v => v && v.uniqueKey === trackKey);
        if (vIdx < 0) return;

        const chosen = grp.variants[vIdx];
        state.searchResults[idx] = Object.assign({}, chosen, {
          groupKey: grp.groupKey || groupKey,
          selectedIndex: vIdx,
          variants: grp.variants
        });
        refreshTabBodyOnly();
        sendToHost('search.selectVariant', { groupKey, trackKey, play: Boolean(autoPlay) });
      }

      function renderTrackRow(track, listKind, idx, playlistId) {
        const key = track.uniqueKey || '';
        const dragKey = listKind === 'search' ? (track.groupKey || key) : key;
        const dragKind = listKind === 'playlist' ? 'playlist-track' : listKind;
        const isCur = state.currentTrack && state.currentTrack.uniqueKey === key;
        const src = getSourceMeta(track.brandKey);
        const fav = Boolean(track.isLiked);
        const dl = Boolean(track.isDownloaded);
        const dlBusy = Boolean(track.isDownloading);

        const thumbInner = track.coverUrl
          ? '<img src="' + esc(track.coverUrl) + '" alt="" loading="lazy" />'
          : ICONS.note;

        let variantsHtml = '';
        if (listKind === 'search' && Array.isArray(track.variants) && track.variants.length > 1) {
          variantsHtml = '<div class="ow-variant-pills">' +
            track.variants.map(v => {
              const vs = getSourceMeta(v.brandKey);
              const isSel = v.uniqueKey === key;
              return '<button data-var-group="' + esc(track.groupKey || key) + '" data-var-key="' + esc(v.uniqueKey) + '" class="ow-var-chip ' + (isSel ? 'is-selected' : '') + '" title="Выбрать источник: ' + esc(v.source || vs.fullName) + '">' +
                '<span class="ow-dot" style="background:' + vs.color + '"></span>' +
                '<span>' + esc(vs.badge) + '</span>' +
              '</button>';
            }).join('') +
          '</div>';
        }

        const dragAttr = (listKind === 'search' || listKind === 'favorites' || listKind === 'playlist')
          ? (' draggable="true" data-drag-kind="' + esc(dragKind) + '" data-drag-key="' + esc(dragKey) + '"')
          : '';

        return '<div data-track-row="' + esc(key) + '" data-list="' + esc(listKind) + '" data-index="' + idx + '"' +
               (playlistId ? ' data-playlist-id="' + esc(playlistId) + '"' : '') +
               dragAttr +
               ' class="ow-track-row ' + (isCur ? 'is-current' : '') + '">' +
            '<div class="ow-row-thumb">' +
              thumbInner +
              (isCur ? '<span class="ow-row-eq-overlay"><span class="' + eqClass() + '"><span></span><span></span><span></span></span></span>' : '') +
            '</div>' +
            '<div class="ow-row-meta">' +
              '<div class="ow-row-title truncate">' + esc(track.title) + '</div>' +
              '<div class="ow-row-sub">' +
                '<span class="ow-dot" style="background:' + src.color + '" title="' + esc(track.source || src.fullName) + '"></span>' +
                '<span class="truncate">' + esc(track.artist) + '</span>' +
              '</div>' +
              variantsHtml +
            '</div>' +
            '<span class="ow-row-dur ow-mono">' + esc(track.durationText || fmtMs(track.durationMs)) + '</span>' +
            (listKind === 'favorites'
              ? '<button data-fav-key="' + esc(key) + '" class="ow-icon-btn ow-row-fav-static is-liked" title="Убрать из избранного">' + ICONS.heartOn + '</button>'
              : '') +
            '<div class="ow-row-actions">' +
              (listKind === 'favorites'
                ? '<button data-export-yandex="' + esc(key) + '" class="ow-icon-btn is-yandex" title="Перенести в Яндекс «Мне нравится»">' + ICONS.sparkle + '</button>'
                : '') +
              '<button data-fav-key="' + esc(key) + '" class="ow-icon-btn ' + (fav ? 'is-liked' : '') + '" title="' + (fav ? 'Убрать из избранного' : 'В избранное') + '">' + (fav ? ICONS.heartOn : ICONS.heartOff) + '</button>' +
              '<button data-add-queue="' + esc(key) + '" class="ow-icon-btn" title="Добавить в очередь">' + ICONS.plus + '</button>' +
              '<button data-dl-key="' + esc(key) + '" class="ow-icon-btn ' + (dl ? 'is-downloaded' : '') + '" title="' + (dl ? 'Показать файл' : (dlBusy ? 'Скачивание...' : 'Скачать')) + '">' + (dl ? ICONS.check : ICONS.download) + '</button>' +
              (playlistId
                ? '<button data-pl-rem-key="' + esc(key) + '" data-playlist-id="' + esc(playlistId) + '" class="ow-icon-btn is-danger" title="Удалить из плейлиста">' + ICONS.close + '</button>'
                : '') +
            '</div>' +
          '</div>';
      }

      /* ======================================================================
         TAB BODIES: SEARCH, QUEUE, FAVORITES/PLAYLISTS, SETTINGS/BRIDGE
         ====================================================================== */
      function buildSearchHeaderHTML() {
        return '<div class="ow-search-header">' +
          '<div class="ow-search-box">' +
            '<span class="ow-search-icon">' + ICONS.search + '</span>' +
            '<input id="ow-search-input" data-role="search-input" type="text" value="' + esc(state.searchQuery) + '" placeholder="Поиск по сервисам или ссылка..." class="ow-search-input" />' +
            '<button data-action="clear-search-input" class="ow-clear-input-btn" style="display:' + (state.searchQuery ? 'flex' : 'none') + '" title="Очистить">' + ICONS.close + '</button>' +
            '<kbd class="ow-kbd-hint ow-mono" style="display:' + (state.searchQuery ? 'none' : 'inline-block') + '">/</kbd>' +
          '</div>' +
          '<div class="ow-chips-scroller" data-chips-scroller="search">' +
            '<button type="button" data-chips-nav="-1" class="ow-chips-nav is-left is-hidden" title="Листать влево">‹</button>' +
            '<div class="ow-chips-row" data-chips-row="search">' +
              FILTER_CHIPS.map(fid => {
                const s = SOURCES[fid];
                const act = state.searchFilter === fid;
                return '<button data-search-src="' + fid + '" class="ow-chip ' + (act ? 'is-active' : '') + '">' +
                  (fid !== 'all' ? '<span class="ow-dot" style="background:' + s.color + '"></span>' : '') +
                  '<span>' + s.badge + '</span>' +
                '</button>';
              }).join('') +
            '</div>' +
            '<button type="button" data-chips-nav="1" class="ow-chips-nav is-right is-hidden" title="Листать вправо">›</button>' +
          '</div>' +
          '<div class="ow-subbar">' +
            '<div style="display:flex;align-items:center;gap:4px">' +
              '<button data-action="start-yandex-wave" class="ow-chip is-action"><span class="ow-dot" style="background:var(--src-ym)"></span><span>Моя волна</span></button>' +
              '<button data-action="start-vk-mix" class="ow-chip is-action"><span class="ow-dot" style="background:var(--src-vk)"></span><span>VK Микс</span></button>' +
            '</div>' +
            '<span data-role="search-status" class="truncate" style="text-align:right">' + esc(state.searchStatus) + '</span>' +
          '</div>' +
        '</div>';
      }

      function buildSearchListHTML() {
        if (state.searchLoading && state.searchResults.length === 0) {
          return '<div class="ow-empty">Ищем треки по сервисам...</div>';
        }
        if (state.searchResults.length === 0) {
          if (state.searchQuery.trim()) {
            return '<div class="ow-empty"><div>Треки не найдены</div>' +
              '<button data-action="clear-search" class="ow-badge-btn" style="margin-top:8px">Сбросить фильтр</button></div>';
          }
          return '<div class="ow-empty">Введите название трека, артиста<br/>или вставьте ссылку Spotify / YouTube / SoundCloud</div>';
        }
        return '<div class="ow-track-list">' +
          state.searchResults.map((tr, idx) => renderTrackRow(tr, 'search', idx, null)).join('') +
          (state.canLoadMore && !state.searchLoading
            ? '<button data-action="load-more" class="ow-btn-secondary" style="margin-top:6px">Загрузить ещё</button>'
            : (state.searchLoading ? '<div class="ow-empty" style="padding:10px">Загружаем ещё...</div>' : '')) +
          '</div>';
      }

      function buildQueueHTML() {
        const items = state.queue || [];
        let curIdx = items.findIndex(t => t.isCurrentlyPlaying);
        if (curIdx < 0 && state.currentTrack) {
          curIdx = items.findIndex(t => t.uniqueKey === state.currentTrack.uniqueKey);
        }
        const curTrack = (curIdx >= 0 ? items[curIdx] : null) || state.currentTrack;

        let nowHtml = '';
        if (curTrack) {
          const src = getSourceMeta(curTrack.brandKey);
          const thumb = curTrack.coverUrl
            ? '<img src="' + esc(curTrack.coverUrl) + '" alt="" />'
            : ICONS.note;
          nowHtml =
            '<div>' +
              '<div class="ow-section-head"><span class="ow-section-label">Сейчас играет</span></div>' +
              '<div class="ow-now-card">' +
                '<div class="ow-row-thumb">' + thumb +
                  '<span class="ow-row-eq-overlay"><span class="' + eqClass() + '"><span></span><span></span><span></span></span></span>' +
                '</div>' +
                '<div class="ow-row-meta">' +
                  '<div class="ow-row-title truncate" style="color:#FFFFFF">' + esc(curTrack.title) + '</div>' +
                  '<div class="ow-row-sub">' +
                    '<span class="ow-dot" style="background:' + src.color + '"></span>' +
                    '<span class="truncate">' + esc(curTrack.artist) + '</span>' +
                  '</div>' +
                '</div>' +
                '<span class="ow-row-dur ow-mono">' + esc(curTrack.durationText || fmtMs(curTrack.durationMs)) + '</span>' +
              '</div>' +
            '</div>';
        }

        const total = items.length;
        return '<div class="ow-stack">' +
          nowHtml +
          '<div>' +
            '<div class="ow-section-head">' +
              '<span class="ow-section-label">Очередь (' + total + ')</span>' +
              (total > 0 ? '<button data-action="clear-queue" class="ow-link-btn">Очистить</button>' : '') +
            '</div>' +
            (total === 0
              ? '<div class="ow-empty-dashed">Очередь пуста — добавьте треки кнопкой «+» из Поиска</div>'
              : '<div class="ow-track-list">' +
                  items.map((tr, idx) => {
                    const isCur = idx === curIdx;
                    const src = getSourceMeta(tr.brandKey);
                    const thumb = tr.coverUrl ? '<img src="' + esc(tr.coverUrl) + '" alt="" loading="lazy" />' : ICONS.note;
                    return '<div data-queue-play="' + idx + '" draggable="true" data-drag-kind="queue" data-drag-index="' + idx + '" class="ow-track-row ' + (isCur ? 'is-current' : '') + '">' +
                      '<div class="ow-row-thumb">' + thumb +
                        (isCur ? '<span class="ow-row-eq-overlay"><span class="' + eqClass() + '"><span></span><span></span><span></span></span></span>' : '') +
                      '</div>' +
                      '<div class="ow-row-meta">' +
                        '<div class="ow-row-title truncate">' + esc(tr.title) + '</div>' +
                        '<div class="ow-row-sub">' +
                          '<span class="ow-dot" style="background:' + src.color + '"></span>' +
                          '<span class="truncate">' + esc(tr.artist) + '</span>' +
                        '</div>' +
                      '</div>' +
                      '<div class="ow-q-actions">' +
                        '<button data-q-move="up" data-q-idx="' + idx + '" class="ow-icon-btn ' + (idx === 0 ? 'is-disabled' : '') + '" title="Вверх">' + ICONS.up + '</button>' +
                        '<button data-q-move="down" data-q-idx="' + idx + '" class="ow-icon-btn ' + (idx === total - 1 ? 'is-disabled' : '') + '" title="Вниз">' + ICONS.down + '</button>' +
                        '<button data-q-rem="' + idx + '" class="ow-icon-btn is-danger" title="Удалить из очереди">' + ICONS.close + '</button>' +
                      '</div>' +
                    '</div>';
                  }).join('') +
                '</div>') +
          '</div>' +
        '</div>';
      }

      function buildFavoritesHeaderHTML() {
        let html = '<div class="ow-search-header">' +
          '<div class="ow-seg-group">' +
            '<button data-fav-sub="tracks" class="ow-seg-btn ' + (state.favSubTab === 'tracks' ? 'is-active' : '') + '">Треки (' + state.favorites.length + ')</button>' +
            '<button data-fav-sub="playlists" class="ow-seg-btn ' + (state.favSubTab === 'playlists' ? 'is-active' : '') + '">Плейлисты (' + state.playlists.length + ')</button>' +
          '</div>';

        if (state.favSubTab === 'tracks') {
          html +=
            '<div class="ow-chips-scroller" data-chips-scroller="fav">' +
              '<button type="button" data-chips-nav="-1" class="ow-chips-nav is-left is-hidden" title="Листать влево">‹</button>' +
              '<div class="ow-chips-row" data-chips-row="fav">' +
                ['all', 'yandex', 'vk', 'spotify', 'soundcloud', 'youtube'].map(fid => {
                  const s = SOURCES[fid];
                  const act = state.favFilter === fid;
                  return '<button data-fav-src="' + fid + '" class="ow-chip ' + (act ? 'is-active' : '') + '">' +
                    (fid !== 'all' ? '<span class="ow-dot" style="background:' + s.color + '"></span>' : '') +
                    '<span>' + s.badge + '</span>' +
                  '</button>';
                }).join('') +
              '</div>' +
              '<button type="button" data-chips-nav="1" class="ow-chips-nav is-right is-hidden" title="Листать вправо">›</button>' +
            '</div>' +
            '<div class="ow-chips-row">' +
              '<button data-action="fav-play-all" class="ow-chip is-action">▶ Все</button>' +
              '<button data-action="fav-shuffle" class="ow-chip is-action">⇄ Микс</button>' +
              (state.accounts.yandexConnected ? '<button data-action="fav-export-yandex" class="ow-chip is-action">✦ В Яндекс</button>' : '') +
            '</div>';
        } else if (!state.openedPlaylistId) {
          html +=
            '<div class="ow-pl-create">' +
              '<input id="ow-new-pl-input" type="text" placeholder="Новый плейлист..." class="ow-pl-input" />' +
              '<button data-action="pl-create" class="ow-badge-btn" title="Создать">+</button>' +
              '<button data-action="pl-sync" class="ow-badge-btn" title="Синхронизировать с аккаунтами">↻ Синхр.</button>' +
            '</div>';
        }
        html += '</div>';
        return html;
      }

      function buildFavoritesBodyHTML() {
        if (state.favSubTab === 'tracks') {
          const list = (state.favorites || []).filter(t => state.favFilter === 'all' || (t.brandKey || '').toLowerCase() === state.favFilter);
          if (list.length === 0) {
            return '<div class="ow-empty">Нет избранных треков в выбранном фильтре</div>';
          }
          return '<div class="ow-track-list">' +
            list.map(tr => {
              const origIdx = state.favorites.findIndex(x => x.uniqueKey === tr.uniqueKey);
              return renderTrackRow(tr, 'favorites', origIdx, null);
            }).join('') +
          '</div>';
        }

        if (state.openedPlaylistId) {
          const pl = state.playlists.find(p => p.id === state.openedPlaylistId);
          if (!pl) {
            state.openedPlaylistId = null;
          } else {
            const tracks = pl.tracks || [];
            return '<div class="ow-stack">' +
              '<div class="ow-section-head">' +
                '<button data-action="pl-back" class="ow-link-btn accent">← К плейлистам</button>' +
                '<button data-pl-play="' + esc(pl.id) + '" class="ow-badge-btn is-ok">▶ Включить</button>' +
              '</div>' +
              '<div style="font-size:13px;font-weight:600;color:#FFFFFF" class="truncate">' + esc(pl.name) + ' · <span style="color:var(--ow-text-secondary);font-weight:400">' + esc(pl.countText || (tracks.length + ' треков')) + '</span></div>' +
              (tracks.length === 0
                ? '<div class="ow-empty-dashed">В этом плейлисте пока нет треков</div>'
                : '<div class="ow-track-list">' +
                    tracks.map((tr, i) => renderTrackRow(tr, 'playlist', i, pl.id)).join('') +
                  '</div>') +
            '</div>';
          }
        }

        if (state.playlists.length === 0) {
          return '<div class="ow-empty-dashed">Нет плейлистов. Создайте свой или синхронизируйте аккаунты!</div>';
        }

        return '<div class="ow-stack" style="gap:6px">' +
          state.playlists.map(pl => {
            const brandCol = pl.showPlatformBadge ? pl.platformBadgeColor : 'var(--ow-text-secondary)';
            const thumb = pl.coverUrl ? '<img src="' + esc(pl.coverUrl) + '" alt="" loading="lazy" />' : ICONS.note;
            return '<div data-pl-open="' + esc(pl.id) + '" draggable="true" data-drag-kind="playlists" data-drag-key="' + esc(pl.id) + '" data-drag-pinned="' + (pl.isPinned ? 'true' : 'false') + '" class="ow-pl-card ' + (pl.isPinned ? 'is-pinned' : '') + '">' +
              '<div class="ow-row-thumb">' + thumb + '</div>' +
              '<div class="ow-row-meta">' +
                '<div class="ow-row-title truncate">' + esc(pl.name) + '</div>' +
                '<div class="ow-row-sub">' +
                  '<span class="ow-dot" style="background:' + esc(brandCol) + '"></span>' +
                  '<span class="truncate">' + esc((pl.showPlatformBadge ? pl.platformBadgeText + ' · ' : '') + (pl.countText || '')) + '</span>' +
                '</div>' +
              '</div>' +
              '<div style="display:flex;align-items:center;gap:3px">' +
                '<button data-pl-pin="' + esc(pl.id) + '" class="ow-icon-btn ' + (pl.isPinned ? 'is-active' : '') + '" title="' + (pl.isPinned ? 'Открепить' : 'Закрепить сверху') + '">' + ICONS.pin + '</button>' +
                '<button data-pl-play="' + esc(pl.id) + '" class="ow-badge-btn" title="Воспроизвести">▶</button>' +
                (!pl.isExternal ? '<button data-pl-del="' + esc(pl.id) + '" class="ow-icon-btn is-danger" title="Удалить плейлист">' + ICONS.close + '</button>' : '') +
              '</div>' +
            '</div>';
          }).join('') +
        '</div>';
      }

      function renderToggleRow(key, label, checked) {
        return '<div class="ow-setting-row">' +
          '<span style="font-size:12px;color:#E5E7EB">' + label + '</span>' +
          '<button data-setting-toggle="' + key + '" role="switch" aria-checked="' + (checked ? 'true' : 'false') + '" class="ow-switch" title="' + label + '"></button>' +
        '</div>';
      }

      function isTypingInSettings() {
        if (state.compact || state.activeTab !== 'settings') return false;
        const el = document.activeElement;
        if (!el) return false;
        const tag = (el.tagName || '').toUpperCase();
        return tag === 'INPUT' || tag === 'TEXTAREA';
      }

      function syncProxyDraftFromServer(force) {
        const p = state.proxy || {};
        if (!force && state.proxyDraft.dirty) return;
        state.proxyDraft = {
          dirty: false,
          enabled: Boolean(p.enabled),
          type: p.type || 'http',
          host: p.host || '',
          port: String(p.port || 8080),
          username: p.username || '',
          password: p.password || '',
          bypassRu: p.bypassRu !== false
        };
      }

      function statusKindClass(kind) {
        if (kind === 'ok') return 'is-ok';
        if (kind === 'warn' || kind === 'busy') return 'is-warn';
        if (kind === 'error') return 'is-err';
        return '';
      }

      function scrollElementIntoViewSoon(selector) {
        setTimeout(() => {
          const el = appRoot.querySelector(selector);
          if (el && el.scrollIntoView) {
            el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
          }
        }, 20);
      }

      function getAuthInstruction(provider, mode) {
        if (provider === 'yandex') {
          if (mode === 'device') {
            return 'Введите одноразовый код в открывшейся вкладке Яндекса (он уже скопирован — просто нажмите Ctrl+V) и подтвердите вход.';
          }
          return 'Подтвердите вход в Яндексе или вставьте токен y0_... вручную ниже.';
        }
        if (provider === 'spotify') {
          if (mode === 'pkce') {
            return 'В открывшейся вкладке браузера нажмите «Принять / Agree» — Spotify подключится к OmniWave автоматически.';
          }
          return 'Подтвердите доступ Spotify в браузере или вставьте cookie sp_dc=... вручную ниже.';
        }
        return '';
      }

      function buildInlineAuthBoxHTML() {
        const ap = state.authPanel || {};
        if (!ap.provider) return '';
        const names = { yandex: 'Яндекс Музыка', spotify: 'Spotify' };
        const ph = ap.provider === 'yandex'
          ? 'Или вставьте токен y0_... / URL вручную'
          : 'Или вставьте cookie sp_dc=... / Bearer BQ...';
        const stCls = ap.statusOk === true ? 'is-ok' : (ap.statusOk === false ? 'is-err' : 'is-warn');

        let specialHtml = '';
        if (ap.provider === 'yandex' && ap.mode === 'device') {
          if (ap.userCode) {
            specialHtml =
              '<div style="display:flex;align-items:center;justify-content:space-between;gap:8px;padding:7px 10px;border-radius:8px;background:rgba(var(--chroma-r),var(--chroma-g),var(--chroma-b),0.14);border:1px solid rgba(var(--chroma-r),var(--chroma-g),var(--chroma-b),0.36)">' +
                '<span class="ow-mono" style="font-size:15px;font-weight:700;letter-spacing:1.4px;color:#FFFFFF">' + esc(ap.userCode) + '</span>' +
                '<button data-action="auth-copy-code" class="ow-badge-btn is-ok">Копировать код</button>' +
              '</div>' +
              '<div style="font-size:10.5px;color:#FBBF24">⏳ Код скопирован — ожидание подтверждения в браузере...</div>';
          } else {
            specialHtml = '<div style="font-size:10.5px;color:#FBBF24">⏳ Запрос одноразового кода входа Яндекс...</div>';
          }
        } else if (ap.provider === 'spotify' && ap.mode === 'pkce') {
          specialHtml = '<div style="font-size:10.5px;color:#4ADE80">⏳ Ожидание подтверждения Spotify в системном браузере...</div>';
        }

        return '<div class="ow-inline-auth">' +
          '<div class="ow-setting-row">' +
            '<span style="font-size:11px;font-weight:600;color:#FFFFFF">Авторизация: ' + esc(names[ap.provider] || ap.provider) + '</span>' +
            '<button data-action="auth-open-again" class="ow-link-btn accent" style="display:inline-flex;align-items:center;gap:4px">' + ICONS.external + ' Браузер</button>' +
          '</div>' +
          '<div style="font-size:10.5px;line-height:1.35;color:var(--ow-text-secondary)">' + esc(getAuthInstruction(ap.provider, ap.mode)) + '</div>' +
          specialHtml +
          '<input data-role="auth-input" type="text" value="' + esc(ap.input || '') + '" placeholder="' + esc(ph) + '" class="ow-input" />' +
          '<div class="ow-input-row">' +
            '<button data-action="auth-paste" class="ow-badge-btn">Вставить</button>' +
            '<button data-action="auth-submit" class="ow-badge-btn is-ok" style="flex:1"' + (ap.busy ? ' disabled' : '') + '>' + (ap.busy ? 'Проверка...' : 'Подключить') + '</button>' +
            '<button data-action="auth-cancel" class="ow-badge-btn">Отмена</button>' +
          '</div>' +
          (ap.statusText ? '<div class="ow-status-line ' + stCls + '">' + esc(ap.statusText) + '</div>' : '') +
        '</div>';
      }

      function buildSettingsHTML() {
        const acc = state.accounts;
        const ws = state.windowSettings;
        const eq = state.eq || { enabled: false, preset: 'Flat', rate: 1.0, presets: {} };
        const pr = state.proxy || {};
        const pd = state.proxyDraft || {};
        const yt = state.ytDlp || {};
        const up = state.update || {};
        const ap = state.authPanel || {};

        const eqPresetNames = ['Flat', 'Bass Boost', 'Rock', 'Electronic', 'Vocal', 'Club', 'Acoustic', 'Late Night'];

        return '<div class="ow-stack">' +
          /* 1. Аккаунты */
          '<div class="ow-card-block" data-section="accounts">' +
            '<div class="ow-section-head" style="margin-bottom:0">' +
              '<span class="ow-section-label">Аккаунты</span>' +
            '</div>' +

            '<div style="display:flex;flex-direction:column;gap:6px">' +
              '<div class="ow-bridge-row">' +
                '<div class="ow-bridge-left">' +
                  '<span class="ow-dot-lg" style="background:var(--src-ym)"></span>' +
                  '<div style="min-width:0">' +
                    '<div style="font-size:12px;color:#E5E7EB">Яндекс Музыка</div>' +
                    '<div class="ow-bridge-sub truncate">' + esc(acc.yandexConnected ? (acc.yandexUserName || 'Подключён') : 'Вход по коду в вашем браузере') + '</div>' +
                  '</div>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:4px;flex-shrink:0">' +
                  (acc.yandexConnected
                    ? '<button data-auth-sync="yandex" class="ow-badge-btn is-ok">Синхр.</button><button data-action="auth-import-yandex-json" class="ow-badge-btn" title="Импорт yandex2spotify JSON">JSON</button><button data-auth-logout="yandex" class="ow-badge-btn">Выйти</button>'
                    : '<button data-action="auth-import-yandex-json" class="ow-badge-btn" title="Импорт yandex2spotify JSON">JSON</button><button data-auth-login="yandex" class="ow-badge-btn">Войти</button>') +
                '</div>' +
              '</div>' +

              '<div class="ow-bridge-row">' +
                '<div class="ow-bridge-left">' +
                  '<span class="ow-dot-lg" style="background:var(--src-vk)"></span>' +
                  '<div style="min-width:0">' +
                    '<div style="font-size:12px;color:#E5E7EB">VK Музыка</div>' +
                    '<div class="ow-bridge-sub truncate">' + esc(acc.vkConnected ? (acc.vkUserName || 'Подключён') : 'Отдельное окно браузера (QR / логин)') + '</div>' +
                  '</div>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:4px;flex-shrink:0">' +
                  (acc.vkConnected
                    ? '<button data-auth-sync="vk" class="ow-badge-btn is-ok">Синхр.</button><button data-auth-logout="vk" class="ow-badge-btn">Выйти</button>'
                    : '<button data-auth-login="vk" class="ow-badge-btn">Войти</button>') +
                '</div>' +
              '</div>' +

              '<div class="ow-bridge-row">' +
                '<div class="ow-bridge-left">' +
                  '<span class="ow-dot-lg" style="background:var(--src-sp)"></span>' +
                  '<div style="min-width:0">' +
                    '<div style="font-size:12px;color:#E5E7EB">Spotify</div>' +
                    '<div class="ow-bridge-sub truncate">' + esc(acc.spotifyConnected ? (acc.spotifyUserName || 'Подключён') : 'Гостевой поиск активен • вход для медиатеки') + '</div>' +
                  '</div>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:4px;flex-shrink:0">' +
                  (acc.spotifyConnected
                    ? '<button data-auth-sync="spotify" class="ow-badge-btn is-ok">Синхр.</button><button data-auth-logout="spotify" class="ow-badge-btn">Выйти</button>'
                    : '<button data-auth-login="spotify" class="ow-badge-btn is-warn">Войти</button>') +
                '</div>' +
              '</div>' +

              '<div class="ow-bridge-row">' +
                '<div class="ow-bridge-left">' +
                  '<span class="ow-dot-lg" style="background:var(--src-yt)"></span>' +
                  '<div style="min-width:0">' +
                    '<div style="font-size:12px;color:#E5E7EB">YouTube (cookies)</div>' +
                    '<div class="ow-bridge-sub truncate">' + esc(acc.youtubeCookiesConnected ? 'Cookies подключены' : (acc.proxyActive ? 'Через прокси • опционально cookies' : 'Поиск работает без входа • опц. cookies')) + '</div>' +
                  '</div>' +
                '</div>' +
                '<div style="display:flex;align-items:center;gap:4px;flex-shrink:0">' +
                  '<button data-auth-login="youtube" class="ow-badge-btn" title="Войти в YouTube в отдельном окне для сохранения cookies">Войти</button>' +
                  '<button data-action="auth-import-yt-cookies" class="ow-badge-btn ' + (acc.youtubeCookiesConnected ? 'is-ok' : '') + '">cookies.txt</button>' +
                  (acc.youtubeCookiesConnected ? '<button data-auth-logout="youtube" class="ow-badge-btn">✕</button>' : '') +
                '</div>' +
              '</div>' +

              '<div class="ow-bridge-row">' +
                '<div class="ow-bridge-left">' +
                  '<span class="ow-dot-lg" style="background:var(--src-sc)"></span>' +
                  '<div style="min-width:0">' +
                    '<div style="font-size:12px;color:#E5E7EB">SoundCloud</div>' +
                    '<div class="ow-bridge-sub truncate">' + esc(acc.proxyActive ? 'Прямой поток + прокси активен' : 'Подключён • встроенный поиск') + '</div>' +
                  '</div>' +
                '</div>' +
                '<span class="ow-badge-btn is-ok" style="cursor:default">Активен</span>' +
              '</div>' +
            '</div>' +

            buildInlineAuthBoxHTML() +

            '<button data-action="pl-sync" class="ow-btn-secondary">↻ Синхронизировать все плейлисты</button>' +
          '</div>' +

          /* 2. Прокси */
          '<div class="ow-card-block" data-section="proxy">' +
            '<div class="ow-section-label">Прокси</div>' +
            renderToggleRow('proxyEnabled', 'Использовать прокси', Boolean(pd.enabled)) +
            '<div class="ow-grid-4">' +
              ['http', 'https', 'socks5', 'socks4'].map(pt =>
                '<button data-proxy-type="' + pt + '" class="ow-chip ' + ((pd.type || 'http') === pt ? 'is-active' : '') + '" style="justify-content:center">' + pt.toUpperCase() + '</button>'
              ).join('') +
            '</div>' +
            '<div class="ow-grid-2">' +
              '<div>' +
                '<div class="ow-field-label">Адрес (хост / URL)</div>' +
                '<input data-proxy-field="host" type="text" value="' + esc(pd.host || '') + '" placeholder="127.0.0.1 или socks5://..." class="ow-input" />' +
              '</div>' +
              '<div>' +
                '<div class="ow-field-label">Порт</div>' +
                '<input data-proxy-field="port" type="text" value="' + esc(pd.port || '') + '" placeholder="8080" class="ow-input ow-mono" />' +
              '</div>' +
            '</div>' +
            '<div class="ow-grid-2">' +
              '<div>' +
                '<div class="ow-field-label">Логин</div>' +
                '<input data-proxy-field="username" type="text" value="' + esc(pd.username || '') + '" placeholder="необязательно" class="ow-input" />' +
              '</div>' +
              '<div>' +
                '<div class="ow-field-label">Пароль</div>' +
                '<div class="ow-input-row" style="gap:4px">' +
                  '<input data-proxy-field="password" type="' + (state.proxyPassVisible ? 'text' : 'password') + '" value="' + esc(pd.password || '') + '" placeholder="необязательно" class="ow-input" />' +
                  '<button data-action="toggle-proxy-pass" class="ow-icon-btn sz-26" title="Показать / скрыть пароль">' + (state.proxyPassVisible ? ICONS.eyeOff : ICONS.eye) + '</button>' +
                '</div>' +
              '</div>' +
            '</div>' +
            renderToggleRow('proxyBypassRu', 'Обходить прокси для Яндекс и VK', pd.bypassRu !== false) +
            '<div data-role="proxy-status" class="ow-status-line ' + statusKindClass(pr.kind) + '">' + esc(pr.text || '') + '</div>' +
            '<div class="ow-btn-grid">' +
              '<button data-action="proxy-test" class="ow-btn-secondary"' + (pr.testing ? ' disabled' : '') + '>' + (pr.testing ? 'Проверка...' : 'Проверить') + '</button>' +
              '<button data-action="proxy-save" class="ow-btn-secondary" style="border-color:rgba(var(--chroma-r),var(--chroma-g),var(--chroma-b),0.45);color:#FFFFFF">Применить</button>' +
            '</div>' +
          '</div>' +

          /* 3. Внешний вид (Chroma Card) */
          '<div class="ow-card-block">' +
            '<div class="ow-section-label">Внешний вид (Chroma Card)</div>' +
            '<div style="display:flex;flex-direction:column;gap:4px">' +
              '<div class="ow-setting-row">' +
                '<span style="font-size:12px;color:#E5E7EB">Интенсивность Chroma</span>' +
                '<span data-role="chroma-label" class="ow-mono" style="font-size:11px;color:var(--ow-text-secondary)">' + ws.chromaIntensity + '%</span>' +
              '</div>' +
              '<input data-role="chroma-slider" type="range" min="0" max="100" value="' + ws.chromaIntensity + '" class="ow-slider w-full" aria-label="Интенсивность Chroma" />' +
            '</div>' +
            '<div style="display:flex;flex-direction:column;gap:4px">' +
              '<div class="ow-setting-row">' +
                '<span style="font-size:12px;color:#E5E7EB">Прозрачность окна</span>' +
                '<span data-role="opacity-label" class="ow-mono" style="font-size:11px;color:var(--ow-text-secondary)">' + ws.windowOpacity + '%</span>' +
              '</div>' +
              '<input data-role="opacity-slider" type="range" min="75" max="100" value="' + ws.windowOpacity + '" class="ow-slider w-full" aria-label="Прозрачность окна" />' +
            '</div>' +
          '</div>' +

          /* 4. Поведение Windows 10/11 и Пилюля */
          '<div class="ow-card-block">' +
            '<div class="ow-section-label">Поведение Windows 10/11</div>' +
            renderToggleRow('alwaysOnTop', 'Поверх всех окон', ws.alwaysOnTop) +
            renderToggleRow('compactModeSetting', 'Компактный режим (Chroma Pill)', state.compact) +
            renderToggleRow('compactEdgeAutoHide', 'Прятать пилюлю за край экрана', ws.compactEdgeAutoHide !== false) +
            renderToggleRow('minimizeToTray', 'Сворачивать в трей', ws.minimizeToTray) +
            renderToggleRow('startMinimized', 'Запускать свёрнутым в трей', Boolean(ws.startMinimized)) +
            renderToggleRow('autoPauseOnSwitch', 'Автопауза при переключении', ws.autoPauseOnSwitch !== false) +
            renderToggleRow('autoStart', 'Автозапуск Windows', ws.autoStart) +
            renderToggleRow('zeroBlurMode', 'Zero-Blur режим (0% GPU)', ws.zeroBlurMode) +
            '<button data-action="center-window" class="ow-btn-secondary">Центрировать окно на экране</button>' +
          '</div>' +

          /* 5. Эквалайзер и скорость */
          '<div class="ow-card-block">' +
            '<div class="ow-section-head" style="margin-bottom:0">' +
              '<span class="ow-section-label">Эквалайзер и скорость</span>' +
              '<span class="ow-mono" style="font-size:10px;color:var(--ow-text-secondary)">' + (eq.rate || 1).toFixed(2).replace(/\.00$/, '.0') + 'x</span>' +
            '</div>' +
            '<div class="ow-chips-row">' +
              '<button data-eq-off="true" class="ow-chip ' + (!eq.enabled ? 'is-active' : '') + '">Выкл</button>' +
              eqPresetNames.map(p =>
                '<button data-eq-preset="' + esc(p) + '" class="ow-chip ' + (eq.enabled && eq.preset === p ? 'is-active' : '') + '">' + esc(p) + '</button>'
              ).join('') +
            '</div>' +
            '<div class="ow-chips-row">' +
              [0.75, 1.0, 1.25, 1.5].map(r =>
                '<button data-rate="' + r + '" class="ow-chip ' + (Math.abs((eq.rate || 1) - r) < 0.02 ? 'is-active' : '') + '">' + r + 'x</button>'
              ).join('') +
            '</div>' +
          '</div>' +

          /* 6. Загрузки и система (yt-dlp, обновления с GitHub) */
          '<div class="ow-card-block" data-section="system">' +
            '<div class="ow-section-label">Загрузки и система</div>' +
            '<div>' +
              '<div class="ow-field-label">Папка загрузок</div>' +
              '<div class="ow-input-row">' +
                '<input data-role="dl-folder-input" type="text" value="' + esc(ws.downloadFolder || '') + '" class="ow-input" />' +
                '<button data-action="dl-pick" class="ow-badge-btn">Выбрать</button>' +
                '<button data-action="dl-open" class="ow-icon-btn sz-26" title="Открыть папку">' + ICONS.folder + '</button>' +
              '</div>' +
            '</div>' +
            '<div class="ow-setting-row">' +
              '<div style="min-width:0;flex:1">' +
                '<div style="font-size:11.5px;color:#E5E7EB">yt-dlp (YouTube)</div>' +
                '<div data-role="ytdlp-status" class="ow-status-line ' + (yt.ok ? 'is-ok' : '') + ' truncate">' + esc(yt.text || '') + '</div>' +
              '</div>' +
              '<button data-action="check-ytdlp" class="ow-badge-btn"' + (yt.busy ? ' disabled' : '') + '>' + (yt.busy ? '...' : 'Проверить') + '</button>' +
            '</div>' +
            '<div style="display:flex;flex-direction:column;gap:5px;border-top:1px solid var(--ow-hairline-soft);padding-top:7px">' +
              '<div style="font-size:11.5px;color:#E5E7EB">Обновления OmniWave (GitHub)</div>' +
              '<div data-role="update-status" class="ow-status-line ' + (up.isAvailable ? 'is-warn' : (up.checkedOk ? 'is-ok' : '')) + '">' + esc(up.text || '') + '</div>' +
              '<div data-role="update-progress" class="ow-progress-bar" style="display:' + (up.busy && up.progress > 0 ? 'block' : 'none') + '">' +
                '<div class="ow-progress-fill" style="width:' + Math.max(0, Math.min(100, up.progress || 0)) + '%"></div>' +
              '</div>' +
              '<div class="ow-btn-grid">' +
                '<button data-action="check-update" class="ow-btn-secondary"' + (up.busy ? ' disabled' : '') + '>Проверить</button>' +
                '<button data-action="install-update" class="ow-btn-secondary" style="' + (up.isAvailable ? 'border-color:rgba(var(--chroma-r),var(--chroma-g),var(--chroma-b),0.55);color:#FFFFFF' : 'opacity:0.45') + '"' + ((!up.isAvailable || up.busy) ? ' disabled' : '') + '>Обновить</button>' +
              '</div>' +
            '</div>' +
          '</div>' +

          /* 7. Горячие клавиши и телеметрия */
          '<div class="ow-card-block">' +
            '<div class="ow-kbd-legend">' +
              '<span class="ow-kbd-item"><kbd class="ow-kbd-key">Space</kbd>Пауза</span>' +
              '<span class="ow-kbd-item"><kbd class="ow-kbd-key">←/→</kbd>Трек</span>' +
              '<span class="ow-kbd-item"><kbd class="ow-kbd-key">/</kbd>Поиск</span>' +
              '<span class="ow-kbd-item"><kbd class="ow-kbd-key">M</kbd>Режим</span>' +
            '</div>' +
            '<div class="ow-telemetry-row">' +
              '<span class="ow-telemetry-left">' +
                '<span class="ow-dot" style="background:var(--status-ok)"></span>' +
                '<span data-role="telemetry-text">' + formatTelemetryText() + '</span>' +
              '</span>' +
              '<span style="color:var(--ow-text-muted)">v2.0</span>' +
            '</div>' +
          '</div>' +
        '</div>';
      }

      function formatTelemetryText() {
        const tm = state.telemetry || { ramMb: 24, cpuPercent: 0.2, gpuPercent: 0 };
        return 'RAM ' + Math.round(tm.ramMb || 24) + ' MB · CPU ' + Number(tm.cpuPercent || 0).toFixed(1) + '% · GPU ' + (tm.gpuPercent || 0) + '%';
      }

      function buildActiveTabBodyHTML() {
        if (state.activeTab === 'search') return buildSearchListHTML();
        if (state.activeTab === 'queue') return buildQueueHTML();
        if (state.activeTab === 'favorites') return buildFavoritesBodyHTML();
        if (state.activeTab === 'settings') return buildSettingsHTML();
        return '';
      }

      function buildActiveTabPanelHTML() {
        if (state.activeTab === 'search') {
          return buildSearchHeaderHTML() + '<div data-role="tab-scroll-body" class="ow-scroll">' + buildSearchListHTML() + '</div>';
        }
        if (state.activeTab === 'favorites') {
          return buildFavoritesHeaderHTML() + '<div data-role="tab-scroll-body" class="ow-scroll">' + buildFavoritesBodyHTML() + '</div>';
        }
        return '<div data-role="tab-scroll-body" class="ow-scroll">' + buildActiveTabBodyHTML() + '</div>';
      }

      function renderApp() {
        const ws = state.windowSettings || {};
        appRoot.setAttribute('data-mode', state.compact ? 'compact' : 'expanded');
        appRoot.setAttribute('data-edge', ws.dockedEdge || '');
        appRoot.setAttribute('data-edge-collapsed', ws.edgeCollapsed ? 'true' : 'false');

        const tr = state.currentTrack;
        const title = tr ? tr.title : 'OmniWave';
        const artist = tr ? tr.artist : 'Выберите или найдите трек';
        const srcMeta = tr ? getSourceMeta(tr.brandKey) : SOURCES.all;
        const ratio = state.totalMs > 0 ? Math.max(0, Math.min(1, state.currentMs / state.totalMs)) : 0;

        if (state.compact) {
          const isDocked = Boolean(ws.dockedEdge);
          appRoot.innerHTML =
            '<div data-role="compact-pill" class="ow-compact-pill">' +
              '<div class="ow-compact-top ow-drag-region" data-drag="true">' +
                '<div data-role="cover-compact" class="ow-cover-frame compact">' + coverMarkup() + '</div>' +
                '<div class="ow-compact-meta">' +
                  '<div data-role="title" class="ow-compact-title truncate">' + esc(title) + '</div>' +
                  '<div class="ow-compact-sub">' +
                    '<span data-role="source-dot" class="ow-dot" style="background:' + srcMeta.color + '"></span>' +
                    '<span data-role="artist" class="truncate">' + esc(artist) + '</span>' +
                  '</div>' +
                '</div>' +
                '<div class="ow-compact-actions">' +
                  '<button data-action="prev" class="ow-icon-btn sz-26" title="Предыдущий трек">' + ICONS.prev + '</button>' +
                  '<button data-action="toggle-play" class="ow-play-btn compact" title="Пуск / Пауза [Space]">' + (state.isPlaying ? ICONS.pause : ICONS.play) + '</button>' +
                  '<button data-action="next" class="ow-icon-btn sz-26" title="Следующий трек">' + ICONS.next + '</button>' +
                  '<button data-action="toggle-edge-dock" data-role="dock-edge-btn" class="ow-icon-btn sz-26 ' + (isDocked ? 'is-active' : '') + '" title="' + (isDocked ? 'Открепить от края экрана' : 'Спрятать за край экрана') + '">' + ICONS.dockEdge + '</button>' +
                  '<button data-action="toggle-mode" class="ow-icon-btn sz-26" title="Развернуть [M]">' + ICONS.expand + '</button>' +
                '</div>' +
              '</div>' +
              '<div class="ow-compact-vol-row" data-no-drag="true">' +
                '<button data-action="toggle-mute" data-role="vol-icon" class="ow-icon-btn" style="width:20px;height:20px" title="Без звука">' + getVolIcon(state.volume) + '</button>' +
                '<input data-role="vol-slider" type="range" min="0" max="100" value="' + state.volume + '" class="ow-slider ow-compact-vol-slider" aria-label="Громкость" />' +
                '<span data-role="vol-pct" class="ow-mono ow-compact-pct">' + state.volume + '%</span>' +
                '<span class="ow-mono ow-compact-time"><span data-role="time-current">' + fmtMs(state.currentMs) + '</span> / <span data-role="time-total">' + fmtMs(state.totalMs) + '</span></span>' +
              '</div>' +
              '<div data-role="scrub" class="ow-pill-prog-zone">' +
                '<div data-role="scrub-tip" class="ow-scrub-tooltip">00:00</div>' +
                '<div class="ow-pill-prog-track"><div data-role="progress-fill" class="ow-scrub-fill" style="transform:scaleX(' + ratio.toFixed(4) + ')"></div></div>' +
              '</div>' +
            '</div>';
          bindInteractiveHandlers();
          return;
        }

        const bridge = getBridgeMeta();
        const liked = Boolean(tr && tr.isLiked);
        const tabs = [
          { id: 'search',    label: 'Поиск',     count: null },
          { id: 'queue',     label: 'Очередь',   count: state.queue.length },
          { id: 'favorites', label: 'Избранное', count: state.favorites.length },
          { id: 'settings',  label: 'Настройки', count: null }
        ];

        appRoot.innerHTML =
          '<div class="ow-titlebar ow-drag-region" data-drag="true">' +
            '<div class="ow-brand-group">' +
              '<span style="color:rgba(255,255,255,0.9);display:flex">' + ICONS.wave + '</span>' +
              '<span class="ow-brand-title">OmniWave</span>' +
              '<span data-role="bridge-dot" class="ow-bridge-dot" style="background:' + bridge.color + '" title="' + esc(bridge.label) + '"></span>' +
            '</div>' +
            '<div class="ow-titlebar-actions">' +
              '<button data-action="toggle-pin" data-role="pin-btn" class="ow-icon-btn ' + (state.windowSettings.alwaysOnTop ? 'is-active' : '') + '" title="Поверх всех окон">' + ICONS.pin + '</button>' +
              '<button data-action="toggle-mode" class="ow-icon-btn" title="Компактный режим [M]">' + ICONS.compact + '</button>' +
              '<button data-action="tray-min" class="ow-icon-btn" title="Свернуть в трей">' + ICONS.minimize + '</button>' +
              '<button data-action="tray-close" class="ow-icon-btn is-close" title="Закрыть">' + ICONS.close + '</button>' +
            '</div>' +
          '</div>' +

          '<div class="ow-hero">' +
            '<div class="ow-hero-top">' +
              '<div data-role="cover-hero" class="ow-cover-frame hero">' + coverMarkup() + '</div>' +
              '<div class="ow-hero-meta">' +
                '<div class="ow-hero-title-row">' +
                  '<div style="min-width:0;flex:1">' +
                    '<div data-role="title" class="ow-hero-title truncate">' + esc(title) + '</div>' +
                    '<div data-role="artist" class="ow-hero-artist truncate">' + esc(artist) + '</div>' +
                  '</div>' +
                  '<button data-action="toggle-fav" data-role="hero-fav-btn" class="ow-icon-btn sz-32 ' + (liked ? 'is-liked' : '') + '" title="Избранное">' + (liked ? ICONS.heartOn : ICONS.heartOff) + '</button>' +
                '</div>' +
                '<div class="ow-source-pill-row">' +
                  '<span class="ow-service-pill">' +
                    '<span data-role="source-dot" class="ow-dot" style="background:' + srcMeta.color + '"></span>' +
                    '<span data-role="source-badge">' + esc(tr ? (tr.source || srcMeta.badge) : 'Готов') + '</span>' +
                  '</span>' +
                '</div>' +
              '</div>' +
            '</div>' +

            '<div class="ow-prog-wrap">' +
              '<div data-role="scrub" class="ow-scrub-zone">' +
                '<div data-role="scrub-tip" class="ow-scrub-tooltip">00:00</div>' +
                '<div class="ow-scrub-track"><div data-role="progress-fill" class="ow-scrub-fill" style="transform:scaleX(' + ratio.toFixed(4) + ')"></div></div>' +
              '</div>' +
              '<div class="ow-times-row ow-mono">' +
                '<span data-role="time-current" class="ow-time-cur">' + fmtMs(state.currentMs) + '</span>' +
                '<span data-role="time-total">' + fmtMs(state.totalMs) + '</span>' +
              '</div>' +
            '</div>' +

            '<div class="ow-controls-row">' +
              '<div class="ow-transport-group">' +
                '<button data-action="toggle-shuffle" data-role="shuffle-btn" class="ow-icon-btn sz-32 ' + (state.shuffle ? 'is-active' : '') + '" title="Перемешать">' + ICONS.shuffle + '</button>' +
                '<button data-action="prev" class="ow-icon-btn sz-32" title="Предыдущий [←]">' + ICONS.prev + '</button>' +
                '<button data-action="toggle-play" class="ow-play-btn" title="Пуск / Пауза [Space]">' + (state.isPlaying ? ICONS.pause : ICONS.play) + '</button>' +
                '<button data-action="next" class="ow-icon-btn sz-32" title="Следующий [→]">' + ICONS.next + '</button>' +
                '<button data-action="cycle-repeat" data-role="repeat-btn" class="ow-icon-btn sz-32 ' + (state.repeatMode !== 0 ? 'is-active' : '') + '" title="Повтор">' + (state.repeatMode === 2 ? ICONS.repeatOne : ICONS.repeat) + '</button>' +
              '</div>' +
              '<div class="ow-vol-wrap">' +
                '<span data-role="vol-tip" class="ow-vol-tip">' + state.volume + '%</span>' +
                '<button data-action="toggle-mute" data-role="vol-icon" class="ow-icon-btn sz-28" title="Без звука">' + getVolIcon(state.volume) + '</button>' +
                '<input data-role="vol-slider" type="range" min="0" max="100" value="' + state.volume + '" class="ow-slider w-vol" aria-label="Громкость" />' +
              '</div>' +
            '</div>' +
          '</div>' +

          '<div class="ow-tabs-bar">' +
            '<div class="ow-tabs-grid">' +
              tabs.map(t =>
                '<button data-tab="' + t.id + '" class="ow-tab-btn ' + (state.activeTab === t.id ? 'is-active' : '') + '">' +
                  '<span>' + t.label + '</span>' +
                  (t.count !== null ? '<span data-tab-badge="' + t.id + '" class="ow-tab-badge ow-mono">' + t.count + '</span>' : '') +
                '</button>'
              ).join('') +
            '</div>' +
          '</div>' +

          '<div data-role="tab-panel" class="ow-tab-panel">' + buildActiveTabPanelHTML() + '</div>';

        bindInteractiveHandlers();

        if (state.pendingFocusSearch) {
          state.pendingFocusSearch = false;
          const sInp = document.getElementById('ow-search-input');
          if (sInp) sInp.focus();
        }
        if (state.pendingFocusNewPlaylist) {
          state.pendingFocusNewPlaylist = false;
          const pInp = document.getElementById('ow-new-pl-input');
          if (pInp) pInp.focus();
        }
        if (state.pendingScrollSection && state.activeTab === 'settings') {
          const sec = state.pendingScrollSection;
          state.pendingScrollSection = null;
          setTimeout(() => {
            const targetSec = appRoot.querySelector('[data-section="' + sec + '"]');
            if (targetSec && targetSec.scrollIntoView) {
              targetSec.scrollIntoView({ behavior: 'smooth', block: 'start' });
            }
          }, 30);
        }
      }

      /* Точечное обновление содержимого вкладки с сохранением scrollTop */
      function refreshTabBodyOnly() {
        if (state.compact) return;
        const body = appRoot.querySelector('[data-role="tab-scroll-body"]');
        if (!body) return;
        const prevScroll = body.scrollTop;
        body.innerHTML = buildActiveTabBodyHTML();
        body.scrollTop = prevScroll;
        bindTabInputs();
        const statusEl = appRoot.querySelector('[data-role="search-status"]');
        if (statusEl && state.activeTab === 'search') {
          statusEl.textContent = state.searchStatus || '';
        }
      }

      function refreshWholeTabPanel() {
        if (state.compact) return;
        const panel = appRoot.querySelector('[data-role="tab-panel"]');
        if (!panel) return;
        panel.innerHTML = buildActiveTabPanelHTML();
        bindTabInputs();
      }

      function updateTabBadges() {
        const qBadge = appRoot.querySelector('[data-tab-badge="queue"]');
        const fBadge = appRoot.querySelector('[data-tab-badge="favorites"]');
        if (qBadge) qBadge.textContent = String(state.queue.length);
        if (fBadge) fBadge.textContent = String(state.favorites.length);
        if (state.activeTab === 'favorites') {
          const tSeg = appRoot.querySelector('[data-fav-sub="tracks"]');
          const pSeg = appRoot.querySelector('[data-fav-sub="playlists"]');
          if (tSeg) tSeg.textContent = 'Треки (' + state.favorites.length + ')';
          if (pSeg) pSeg.textContent = 'Плейлисты (' + state.playlists.length + ')';
        }
      }

      function updateTimeUI() {
        const ratio = state.totalMs > 0 ? Math.max(0, Math.min(1, state.currentMs / state.totalMs)) : 0;
        const curStr = fmtMs(state.currentMs);
        const totStr = fmtMs(state.totalMs);
        appRoot.querySelectorAll('[data-role="time-current"]').forEach(el => { el.textContent = curStr; });
        appRoot.querySelectorAll('[data-role="time-total"]').forEach(el => { el.textContent = totStr; });
        appRoot.querySelectorAll('[data-role="progress-fill"]').forEach(el => {
          el.style.transform = 'scaleX(' + ratio.toFixed(4) + ')';
        });
      }

      function updatePlaybackButtonsUI() {
        appRoot.querySelectorAll('[data-action="toggle-play"]').forEach(btn => {
          btn.innerHTML = state.isPlaying ? ICONS.pause : ICONS.play;
        });
        const sh = appRoot.querySelector('[data-role="shuffle-btn"]');
        if (sh) sh.classList.toggle('is-active', Boolean(state.shuffle));
        const rp = appRoot.querySelector('[data-role="repeat-btn"]');
        if (rp) {
          rp.innerHTML = state.repeatMode === 2 ? ICONS.repeatOne : ICONS.repeat;
          rp.classList.toggle('is-active', state.repeatMode !== 0);
        }
        appRoot.querySelectorAll('.ow-eq').forEach(el => {
          el.classList.toggle('paused', !state.isPlaying || state.suspended);
        });
      }

      function updateVolumeUI() {
        appRoot.querySelectorAll('[data-role="vol-slider"]').forEach(el => { el.value = state.volume; });
        appRoot.querySelectorAll('[data-role="vol-icon"]').forEach(el => { el.innerHTML = getVolIcon(state.volume); });
        appRoot.querySelectorAll('[data-role="vol-tip"]').forEach(el => { el.textContent = state.volume + '%'; });
        appRoot.querySelectorAll('[data-role="vol-pct"]').forEach(el => { el.textContent = state.volume + '%'; });
      }

      let isPointerHeld = false;
      function setPointerHold(active) {
        const next = Boolean(active);
        if (isPointerHeld === next) return;
        isPointerHeld = next;
        sendToHost('window.setPointerHold', { active: next });
      }

      function sendProxySave() {
        const pd = state.proxyDraft;
        sendToHost('settings.saveProxy', {
          enabled: Boolean(pd.enabled),
          proxyType: pd.type || 'http',
          host: pd.host || '',
          port: pd.port || '',
          username: pd.username || '',
          password: pd.password || '',
          bypassRu: pd.bypassRu !== false
        });
      }

      function sendProxyTest() {
        const pd = state.proxyDraft;
        sendToHost('settings.testProxy', {
          proxyType: pd.type || 'http',
          host: pd.host || '',
          port: pd.port || '',
          username: pd.username || '',
          password: pd.password || ''
        });
      }

      function updateHeroMetaUI() {
        const tr = state.currentTrack;
        const srcMeta = tr ? getSourceMeta(tr.brandKey) : SOURCES.all;
        appRoot.querySelectorAll('[data-role="title"]').forEach(el => { el.textContent = tr ? tr.title : 'OmniWave'; });
        appRoot.querySelectorAll('[data-role="artist"]').forEach(el => { el.textContent = tr ? tr.artist : 'Выберите или найдите трек'; });
        appRoot.querySelectorAll('[data-role="source-badge"]').forEach(el => { el.textContent = tr ? (tr.source || srcMeta.badge) : 'Готов'; });
        appRoot.querySelectorAll('[data-role="source-dot"]').forEach(el => { el.style.background = srcMeta.color; });
        const heroCov = appRoot.querySelector('[data-role="cover-hero"]');
        if (heroCov) heroCov.innerHTML = coverMarkup();
        const cmpCov = appRoot.querySelector('[data-role="cover-compact"]');
        if (cmpCov) cmpCov.innerHTML = coverMarkup();
        const heroFav = appRoot.querySelector('[data-role="hero-fav-btn"]');
        if (heroFav) {
          const liked = Boolean(tr && tr.isLiked);
          heroFav.innerHTML = liked ? ICONS.heartOn : ICONS.heartOff;
          heroFav.classList.toggle('is-liked', liked);
        }
      }

      function bindTabInputs() {
        const searchInp = appRoot.querySelector('[data-role="search-input"]');
        if (searchInp) {
          searchInp.oninput = (e) => {
            state.searchQuery = e.target.value;
            const clr = appRoot.querySelector('[data-action="clear-search-input"]');
            const kbd = appRoot.querySelector('.ow-kbd-hint');
            if (clr) clr.style.display = state.searchQuery ? 'flex' : 'none';
            if (kbd) kbd.style.display = state.searchQuery ? 'none' : 'inline-block';

            clearTimeout(searchDebounceTimer);
            const qTrim = state.searchQuery.trim();
            if (!qTrim && state.searchFilter !== 'radio') {
              state.searchResults = [];
              state.searchStatus = '';
              state.canLoadMore = false;
              state.searchLoading = false;
              refreshTabBodyOnly();
              return;
            }
            searchDebounceTimer = setTimeout(() => {
              sendToHost('search.run', { query: state.searchQuery.trim(), filter: state.searchFilter });
            }, 220);
          };
          searchInp.onkeydown = (e) => {
            if (e.key === 'Enter') {
              clearTimeout(searchDebounceTimer);
              sendToHost('search.run', { query: state.searchQuery.trim(), filter: state.searchFilter });
            }
          };
        }

        const plInp = document.getElementById('ow-new-pl-input');
        if (plInp) {
          plInp.onkeydown = (e) => {
            if (e.key === 'Enter' && plInp.value.trim()) {
              sendToHost('playlists.create', { name: plInp.value.trim() });
              plInp.value = '';
            }
          };
        }

        const authInp = appRoot.querySelector('[data-role="auth-input"]');
        if (authInp) {
          authInp.oninput = (e) => {
            state.authPanel.input = e.target.value;
          };
          authInp.onkeydown = (e) => {
            if (e.key === 'Enter' && state.authPanel.provider) {
              sendToHost('auth.submitInput', { provider: state.authPanel.provider, input: state.authPanel.input || '' });
            }
          };
        }

        appRoot.querySelectorAll('[data-proxy-field]').forEach(inp => {
          const field = inp.getAttribute('data-proxy-field');
          inp.oninput = (e) => {
            state.proxyDraft.dirty = true;
            state.proxyDraft[field] = e.target.value;
          };
          inp.onkeydown = (e) => {
            if (e.key === 'Enter') {
              inp.blur();
              sendProxySave();
            }
          };
        });

        const dlInp = appRoot.querySelector('[data-role="dl-folder-input"]');
        if (dlInp) {
          dlInp.onkeydown = (e) => {
            if (e.key === 'Enter') {
              dlInp.blur();
              sendToHost('settings.setDownloadFolder', { path: dlInp.value });
            }
          };
          dlInp.onblur = () => {
            if ((dlInp.value || '').trim() !== (state.windowSettings.downloadFolder || '').trim()) {
              sendToHost('settings.setDownloadFolder', { path: dlInp.value });
            }
          };
        }

        const chromaSlider = appRoot.querySelector('[data-role="chroma-slider"]');
        if (chromaSlider) {
          chromaSlider.oninput = (e) => {
            const val = Number(e.target.value);
            state.windowSettings.chromaIntensity = val;
            ChromaEngine.syncStrength();
            const lbl = appRoot.querySelector('[data-role="chroma-label"]');
            if (lbl) lbl.textContent = val + '%';
            clearTimeout(chromaDebounceTimer);
            chromaDebounceTimer = setTimeout(() => {
              sendToHost('window.setChromaIntensity', { intensity: val });
            }, 90);
          };
        }

        const opSlider = appRoot.querySelector('[data-role="opacity-slider"]');
        if (opSlider) {
          opSlider.oninput = (e) => {
            const val = Number(e.target.value);
            state.windowSettings.windowOpacity = val;
            const lbl = appRoot.querySelector('[data-role="opacity-label"]');
            if (lbl) lbl.textContent = val + '%';
            sendToHost('window.setOpacity', { opacity: val });
          };
        }

        bindChipsScrollers();
      }

      function bindChipsScrollers() {
        appRoot.querySelectorAll('.ow-chips-scroller .ow-chips-row').forEach(row => {
          const key = row.getAttribute('data-chips-row') || 'search';
          const scroller = row.closest('.ow-chips-scroller');
          const leftBtn = scroller && scroller.querySelector('.ow-chips-nav.is-left');
          const rightBtn = scroller && scroller.querySelector('.ow-chips-nav.is-right');

          const updateNav = () => {
            const maxScroll = Math.max(0, row.scrollWidth - row.clientWidth);
            const canScroll = maxScroll > 2;
            row.classList.toggle('is-scrollable', canScroll);
            if (leftBtn) leftBtn.classList.toggle('is-hidden', !canScroll || row.scrollLeft <= 2);
            if (rightBtn) rightBtn.classList.toggle('is-hidden', !canScroll || row.scrollLeft >= maxScroll - 2);
          };

          row.style.scrollBehavior = 'auto';
          row.scrollLeft = Number((state.chipsScroll && state.chipsScroll[key]) || 0);
          const actChip = row.querySelector('.ow-chip.is-active');
          if (actChip) {
            const pad = 24;
            const cLeft = actChip.offsetLeft;
            const cRight = cLeft + actChip.offsetWidth;
            if (cLeft - pad < row.scrollLeft) {
              row.scrollLeft = Math.max(0, cLeft - pad);
            } else if (cRight + pad > row.scrollLeft + row.clientWidth) {
              row.scrollLeft = Math.max(0, cRight + pad - row.clientWidth);
            }
          }
          if (!state.chipsScroll) state.chipsScroll = { search: 0, fav: 0 };
          state.chipsScroll[key] = row.scrollLeft;
          row.style.scrollBehavior = '';

          row.onscroll = () => {
            state.chipsScroll[key] = row.scrollLeft;
            updateNav();
          };

          row.onwheel = (e) => {
            if (row.scrollWidth <= row.clientWidth + 2) return;
            const delta = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
            if (!delta) return;
            e.preventDefault();
            row.scrollLeft += delta;
          };

          row.onpointerdown = (e) => {
            if (e.button !== 0 || row.scrollWidth <= row.clientWidth + 2) return;
            const startX = e.clientX;
            const startScroll = row.scrollLeft;
            let dragged = false;

            const onMove = (ev) => {
              if ((ev.buttons & 1) === 0) {
                cleanup();
                return;
              }
              const dx = ev.clientX - startX;
              if (!dragged && Math.abs(dx) >= 5) {
                dragged = true;
                row.classList.add('is-dragging');
              }
              if (dragged) {
                row.scrollLeft = startScroll - dx;
              }
            };

            const cleanup = () => {
              window.removeEventListener('pointermove', onMove);
              window.removeEventListener('pointerup', cleanup);
              window.removeEventListener('pointercancel', cleanup);
              row.classList.remove('is-dragging');
              if (dragged) {
                row.dataset.dragSuppressUntil = String(Date.now() + 260);
              }
            };

            window.addEventListener('pointermove', onMove);
            window.addEventListener('pointerup', cleanup);
            window.addEventListener('pointercancel', cleanup);
          };

          updateNav();
          requestAnimationFrame(updateNav);
        });
      }

      function bindInteractiveHandlers() {
        const pill = appRoot.querySelector('[data-role="compact-pill"]');
        if (pill) {
          pill.ondblclick = (e) => {
            if (e.target.closest('button, input, [data-role="scrub"]')) return;
            sendToHost('window.setCompactMode', { compact: false });
          };
        }

        appRoot.onclick = (e) => {
          const varChip = e.target.closest('[data-var-group]');
          if (varChip) {
            e.stopPropagation();
            const groupKey = varChip.getAttribute('data-var-group');
            const trackKey = varChip.getAttribute('data-var-key');
            const isAlready = varChip.classList.contains('is-selected');
            selectSearchVariant(groupKey, trackKey, isAlready);
            return;
          }

          const exportYa = e.target.closest('[data-export-yandex]');
          if (exportYa) {
            e.stopPropagation();
            sendToHost('track.exportYandex', { trackKey: exportYa.getAttribute('data-export-yandex') });
            return;
          }

          const favBtn = e.target.closest('[data-fav-key]');
          if (favBtn) {
            e.stopPropagation();
            sendToHost('track.toggleLike', { trackKey: favBtn.getAttribute('data-fav-key') });
            return;
          }

          const addQ = e.target.closest('[data-add-queue]');
          if (addQ) {
            e.stopPropagation();
            sendToHost('track.addToQueue', { trackKey: addQ.getAttribute('data-add-queue'), playNext: false });
            return;
          }

          const dlBtn = e.target.closest('[data-dl-key]');
          if (dlBtn) {
            e.stopPropagation();
            sendToHost('track.download', { trackKey: dlBtn.getAttribute('data-dl-key') });
            return;
          }

          const plRem = e.target.closest('[data-pl-rem-key]');
          if (plRem) {
            e.stopPropagation();
            sendToHost('playlists.removeTrack', {
              playlistId: plRem.getAttribute('data-playlist-id'),
              trackKey: plRem.getAttribute('data-pl-rem-key')
            });
            return;
          }

          const trackRow = e.target.closest('[data-track-row]');
          if (trackRow) {
            sendToHost('search.playTrack', {
              list: trackRow.getAttribute('data-list') || 'search',
              playlistId: trackRow.getAttribute('data-playlist-id') || null,
              trackKey: trackRow.getAttribute('data-track-row'),
              index: Number(trackRow.getAttribute('data-index') || -1)
            });
            return;
          }

          const qMove = e.target.closest('[data-q-move]');
          if (qMove) {
            e.stopPropagation();
            const idx = Number(qMove.getAttribute('data-q-idx'));
            const dir = qMove.getAttribute('data-q-move') === 'up' ? -1 : 1;
            sendToHost('queue.move', { from: idx, to: idx + dir });
            return;
          }

          const qRem = e.target.closest('[data-q-rem]');
          if (qRem) {
            e.stopPropagation();
            sendToHost('queue.remove', { index: Number(qRem.getAttribute('data-q-rem')) });
            return;
          }

          const qPlay = e.target.closest('[data-queue-play]');
          if (qPlay) {
            sendToHost('queue.playIndex', { index: Number(qPlay.getAttribute('data-queue-play')) });
            return;
          }

          const plPin = e.target.closest('[data-pl-pin]');
          if (plPin) {
            e.stopPropagation();
            sendToHost('playlists.togglePin', { id: plPin.getAttribute('data-pl-pin') });
            return;
          }

          const plPlay = e.target.closest('[data-pl-play]');
          if (plPlay) {
            e.stopPropagation();
            sendToHost('playlists.play', { id: plPlay.getAttribute('data-pl-play') });
            return;
          }

          const plDel = e.target.closest('[data-pl-del]');
          if (plDel) {
            e.stopPropagation();
            sendToHost('playlists.delete', { id: plDel.getAttribute('data-pl-del') });
            return;
          }

          const plOpen = e.target.closest('[data-pl-open]');
          if (plOpen) {
            state.openedPlaylistId = plOpen.getAttribute('data-pl-open');
            refreshWholeTabPanel();
            return;
          }

          const tabBtn = e.target.closest('[data-tab]');
          if (tabBtn) {
            const nextTab = tabBtn.getAttribute('data-tab');
            if (nextTab === state.activeTab) return;
            state.activeTab = nextTab;
            sendToHost('ui.tabChanged', { tab: nextTab });
            appRoot.querySelectorAll('[data-tab]').forEach(b => b.classList.toggle('is-active', b.getAttribute('data-tab') === nextTab));
            refreshWholeTabPanel();
            return;
          }

          const chipsNav = e.target.closest('[data-chips-nav]');
          if (chipsNav) {
            const scroller = chipsNav.closest('.ow-chips-scroller');
            const row = scroller && scroller.querySelector('.ow-chips-row');
            if (row) {
              const dir = Number(chipsNav.getAttribute('data-chips-nav') || 1);
              row.scrollBy({ left: dir * 130, behavior: 'smooth' });
            }
            return;
          }

          const sSrc = e.target.closest('[data-search-src]');
          if (sSrc) {
            const row = sSrc.closest('.ow-chips-row');
            if (row && Number(row.dataset.dragSuppressUntil || 0) > Date.now()) return;
            if (row && state.chipsScroll) state.chipsScroll.search = row.scrollLeft;
            clearTimeout(searchDebounceTimer);
            state.searchFilter = sSrc.getAttribute('data-search-src');
            state.searchResults = [];
            state.canLoadMore = false;
            const qTrim = state.searchQuery.trim();
            if (!qTrim && state.searchFilter !== 'radio') {
              state.searchLoading = false;
              state.searchStatus = '';
              refreshWholeTabPanel();
              sendToHost('search.run', { query: '', filter: state.searchFilter });
            } else {
              state.searchLoading = true;
              state.searchStatus = state.searchFilter === 'radio' ? 'Загрузка радиостанций...' : 'Поиск...';
              refreshWholeTabPanel();
              sendToHost('search.run', { query: qTrim, filter: state.searchFilter });
            }
            return;
          }

          const favSub = e.target.closest('[data-fav-sub]');
          if (favSub) {
            state.favSubTab = favSub.getAttribute('data-fav-sub');
            state.openedPlaylistId = null;
            refreshWholeTabPanel();
            return;
          }

          const fSrc = e.target.closest('[data-fav-src]');
          if (fSrc) {
            const row = fSrc.closest('.ow-chips-row');
            if (row && Number(row.dataset.dragSuppressUntil || 0) > Date.now()) return;
            if (row && state.chipsScroll) state.chipsScroll.fav = row.scrollLeft;
            state.favFilter = fSrc.getAttribute('data-fav-src');
            refreshWholeTabPanel();
            return;
          }

          const authLogin = e.target.closest('[data-auth-login]');
          if (authLogin) {
            const prov = authLogin.getAttribute('data-auth-login');
            if (prov === 'vk' || prov === 'youtube') {
              state.authPanel = {
                provider: '',
                mode: '',
                userCode: '',
                verificationUrl: '',
                input: '',
                busy: false,
                statusText: '',
                statusOk: null
              };
              refreshTabBodyOnly();
              sendToHost('auth.openBrowser', { provider: prov });
              return;
            }
            state.authPanel = {
              provider: prov,
              mode: '',
              userCode: '',
              verificationUrl: '',
              input: '',
              busy: false,
              statusText: '',
              statusOk: null
            };
            refreshTabBodyOnly();
            scrollElementIntoViewSoon('.ow-inline-auth');
            sendToHost('auth.openBrowser', { provider: prov });
            return;
          }

          const authSync = e.target.closest('[data-auth-sync]');
          if (authSync) {
            sendToHost('auth.syncProvider', { provider: authSync.getAttribute('data-auth-sync') });
            return;
          }

          const authLogout = e.target.closest('[data-auth-logout]');
          if (authLogout) {
            const prov = authLogout.getAttribute('data-auth-logout');
            if (state.authPanel.provider === prov) {
              state.authPanel = {
                provider: '',
                mode: '',
                userCode: '',
                verificationUrl: '',
                input: '',
                busy: false,
                statusText: '',
                statusOk: null
              };
            }
            sendToHost('auth.disconnect', { provider: prov });
            return;
          }

          const proxyTypeBtn = e.target.closest('[data-proxy-type]');
          if (proxyTypeBtn) {
            const nextType = proxyTypeBtn.getAttribute('data-proxy-type');
            const prevDefault = (state.proxyDraft.type || '').startsWith('socks') ? '1080' : (state.proxyDraft.type === 'https' ? '443' : '8080');
            state.proxyDraft.dirty = true;
            state.proxyDraft.type = nextType;
            const curPort = (state.proxyDraft.port || '').trim();
            if (!curPort || curPort === prevDefault || curPort === '8080' || curPort === '1080' || curPort === '443') {
              state.proxyDraft.port = nextType.startsWith('socks') ? '1080' : (nextType === 'https' ? '443' : '8080');
            }
            refreshTabBodyOnly();
            return;
          }

          const sw = e.target.closest('[data-setting-toggle]');
          if (sw) {
            const key = sw.getAttribute('data-setting-toggle');
            if (key === 'proxyEnabled') {
              const nextVal = !state.proxyDraft.enabled;
              state.proxyDraft.dirty = true;
              state.proxyDraft.enabled = nextVal;
              sw.setAttribute('aria-checked', nextVal ? 'true' : 'false');
              if (!nextVal || (state.proxyDraft.host || '').trim()) {
                sendProxySave();
              }
              return;
            }
            if (key === 'proxyBypassRu') {
              const nextVal = !(state.proxyDraft.bypassRu !== false);
              state.proxyDraft.dirty = true;
              state.proxyDraft.bypassRu = nextVal;
              sw.setAttribute('aria-checked', nextVal ? 'true' : 'false');
              if (state.proxyDraft.enabled && (state.proxyDraft.host || '').trim()) {
                sendProxySave();
              }
              return;
            }

            const nextVal = !state.windowSettings[key];
            state.windowSettings[key] = nextVal;
            sw.setAttribute('aria-checked', nextVal ? 'true' : 'false');
            if (key === 'alwaysOnTop') {
              sendToHost('window.setAlwaysOnTop', { alwaysOnTop: nextVal });
              const pinBtn = appRoot.querySelector('[data-role="pin-btn"]');
              if (pinBtn) pinBtn.classList.toggle('is-active', nextVal);
            } else if (key === 'compactModeSetting') {
              sendToHost('window.setCompactMode', { compact: nextVal });
            } else if (key === 'compactEdgeAutoHide') {
              sendToHost('window.setCompactEdgeAutoHide', { enabled: nextVal });
            } else if (key === 'minimizeToTray') {
              sendToHost('window.setMinimizeToTray', { enabled: nextVal });
            } else if (key === 'startMinimized') {
              sendToHost('window.setStartMinimized', { enabled: nextVal });
            } else if (key === 'autoPauseOnSwitch') {
              sendToHost('window.setAutoPauseOnSwitch', { enabled: nextVal });
            } else if (key === 'autoStart') {
              sendToHost('window.setAutoStart', { enabled: nextVal });
            } else if (key === 'zeroBlurMode') {
              ChromaEngine.syncStrength();
              sendToHost('window.setZeroBlur', { enabled: nextVal });
              showToast(nextVal ? 'Zero-Blur режим (0% GPU) активен' : 'Zero-Blur режим выключен');
            }
            return;
          }

          const eqOff = e.target.closest('[data-eq-off]');
          if (eqOff) {
            sendToHost('eq.update', { enabled: false, preset: state.eq.preset || 'Flat', preamp: state.eq.preamp || 0, bands: state.eq.bands || [] });
            return;
          }

          const eqPre = e.target.closest('[data-eq-preset]');
          if (eqPre) {
            sendToHost('eq.applyPreset', { preset: eqPre.getAttribute('data-eq-preset') });
            return;
          }

          const rateBtn = e.target.closest('[data-rate]');
          if (rateBtn) {
            sendToHost('eq.setRate', { rate: Number(rateBtn.getAttribute('data-rate')) });
            return;
          }

          const act = e.target.closest('[data-action]');
          if (!act) return;
          switch (act.getAttribute('data-action')) {
            case 'toggle-play': sendToHost('player.togglePause'); break;
            case 'prev': sendToHost('player.prev'); break;
            case 'next': sendToHost('player.next'); break;
            case 'toggle-shuffle': sendToHost('player.toggleShuffle'); break;
            case 'cycle-repeat': sendToHost('player.cycleRepeat'); break;
            case 'toggle-fav':
              if (state.currentTrack && state.currentTrack.uniqueKey) {
                sendToHost('track.toggleLike', { trackKey: state.currentTrack.uniqueKey });
              }
              break;
            case 'toggle-mute': sendToHost('player.toggleMute'); break;
            case 'toggle-pin':
              sendToHost('window.setAlwaysOnTop', { alwaysOnTop: !state.windowSettings.alwaysOnTop });
              break;
            case 'toggle-mode':
              sendToHost('window.setCompactMode', { compact: !state.compact });
              break;
            case 'toggle-edge-dock':
              sendToHost('window.toggleEdgeDock');
              break;
            case 'tray-min': sendToHost('window.hideToTray'); break;
            case 'tray-close': sendToHost('window.closeAction'); break;
            case 'start-yandex-wave': sendToHost('yandex.startWave'); break;
            case 'start-vk-mix': sendToHost('vk.startMix'); break;
            case 'load-more': sendToHost('search.loadMore', { filter: state.searchFilter }); break;
            case 'clear-search':
              state.searchQuery = '';
              state.searchFilter = 'all';
              state.searchResults = [];
              state.searchStatus = '';
              state.canLoadMore = false;
              refreshWholeTabPanel();
              break;
            case 'clear-search-input':
              clearTimeout(searchDebounceTimer);
              state.searchQuery = '';
              state.searchResults = [];
              state.searchStatus = '';
              state.canLoadMore = false;
              refreshWholeTabPanel();
              const inp = document.getElementById('ow-search-input');
              if (inp) inp.focus();
              break;
            case 'clear-queue': sendToHost('queue.clear'); break;
            case 'fav-play-all': sendToHost('favorites.playAll'); break;
            case 'fav-shuffle': sendToHost('favorites.shuffle'); break;
            case 'fav-export-yandex': sendToHost('favorites.exportYandex'); break;
            case 'pl-create': {
              const plInp = document.getElementById('ow-new-pl-input');
              if (plInp && plInp.value.trim()) {
                sendToHost('playlists.create', { name: plInp.value.trim() });
                plInp.value = '';
              }
              break;
            }
            case 'pl-sync': sendToHost('playlists.sync'); break;
            case 'pl-back':
              state.openedPlaylistId = null;
              refreshWholeTabPanel();
              break;
            case 'open-accounts': sendToHost('window.openAccounts'); break;
            case 'open-settings': sendToHost('window.openSettings'); break;
            case 'center-window': sendToHost('window.center'); break;
            case 'auth-import-yandex-json': sendToHost('auth.importYandexJson'); break;
            case 'auth-import-yt-cookies': sendToHost('auth.importYoutubeCookies'); break;
            case 'auth-open-again':
              if (state.authPanel.provider) sendToHost('auth.openBrowser', { provider: state.authPanel.provider });
              break;
            case 'auth-copy-code':
              if (state.authPanel.userCode) sendToHost('auth.copyCode', { code: state.authPanel.userCode });
              break;
            case 'auth-paste':
              if (state.authPanel.provider) sendToHost('auth.pasteAndSubmit', { provider: state.authPanel.provider });
              break;
            case 'auth-submit':
              if (state.authPanel.provider) sendToHost('auth.submitInput', { provider: state.authPanel.provider, input: state.authPanel.input || '' });
              break;
            case 'auth-cancel':
              {
                const prevProv = state.authPanel.provider || '';
                state.authPanel = {
                  provider: '',
                  mode: '',
                  userCode: '',
                  verificationUrl: '',
                  input: '',
                  busy: false,
                  statusText: '',
                  statusOk: null
                };
                sendToHost('auth.cancelBrowser', { provider: prevProv });
                refreshTabBodyOnly();
              }
              break;
            case 'toggle-proxy-pass':
              state.proxyPassVisible = !state.proxyPassVisible;
              refreshTabBodyOnly();
              break;
            case 'proxy-test': sendProxyTest(); break;
            case 'proxy-save': sendProxySave(); break;
            case 'dl-pick': sendToHost('settings.pickDownloadFolder'); break;
            case 'dl-open': sendToHost('settings.openDownloadFolder'); break;
            case 'check-ytdlp': sendToHost('system.checkYtDlp'); break;
            case 'check-update': sendToHost('system.checkUpdate'); break;
            case 'install-update': sendToHost('system.installUpdate'); break;
          }
        };

        const volSlider = appRoot.querySelector('[data-role="vol-slider"]');
        if (volSlider) {
          volSlider.onpointerdown = (e) => {
            e.stopPropagation();
            if (state.compact) setPointerHold(true);
          };
          volSlider.oninput = (e) => {
            state.volume = Number(e.target.value);
            updateVolumeUI();
            sendToHost('player.setVolume', { volume: state.volume });
          };
        }

        const volWrap = appRoot.querySelector('.ow-vol-wrap, .ow-compact-vol-row');
        if (volWrap) {
          volWrap.onwheel = (e) => {
            e.preventDefault();
            const delta = e.deltaY < 0 ? 5 : -5;
            state.volume = Math.max(0, Math.min(100, state.volume + delta));
            updateVolumeUI();
            sendToHost('player.setVolume', { volume: state.volume });
          };
        }

        appRoot.querySelectorAll('[data-role="scrub"]').forEach(zone => {
          const tip = zone.querySelector('[data-role="scrub-tip"]');
          let scrubbing = false;
          const calcRatio = (clientX) => {
            const rect = zone.getBoundingClientRect();
            if (rect.width <= 0) return 0;
            const r = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
            if (tip) {
              tip.style.left = Math.max(18, Math.min(rect.width - 18, clientX - rect.left)) + 'px';
              tip.textContent = fmtMs(r * state.totalMs);
            }
            return r;
          };
          zone.onpointerdown = (e) => {
            e.stopPropagation();
            scrubbing = true;
            if (state.compact) setPointerHold(true);
            zone.classList.add('is-scrubbing');
            try { zone.setPointerCapture(e.pointerId); } catch (_) {}
            const r = calcRatio(e.clientX);
            if (state.totalMs > 0) {
              state.currentMs = Math.round(r * state.totalMs);
              updateTimeUI();
            }
          };
          zone.onpointermove = (e) => {
            const r = calcRatio(e.clientX);
            if (scrubbing && state.totalMs > 0) {
              state.currentMs = Math.round(r * state.totalMs);
              updateTimeUI();
            }
          };
          const end = (e) => {
            if (!scrubbing) return;
            scrubbing = false;
            setPointerHold(false);
            zone.classList.remove('is-scrubbing');
            const r = calcRatio(e.clientX);
            sendToHost('player.seekFraction', { fraction: r });
          };
          zone.onpointerup = end;
          zone.onpointercancel = () => {
            scrubbing = false;
            setPointerHold(false);
            zone.classList.remove('is-scrubbing');
          };
        });

        bindTabInputs();
      }

      /* Нативный Win32 drag с порогом 4px (сохраняет dblclick на пилюле) */
      (function initNativeDrag() {
        let pending = false;
        let sx = 0, sy = 0;

        appRoot.addEventListener('pointerdown', (e) => {
          if (e.button !== 0) return;
          if (!e.target.closest('.ow-drag-region')) return;
          if (e.target.closest('button, input, [data-role="scrub"], [data-no-drag]')) return;
          pending = true;
          sx = e.clientX;
          sy = e.clientY;
        });

        appRoot.addEventListener('pointermove', (e) => {
          if (!pending) return;
          if ((e.buttons & 1) === 0) { pending = false; return; }
          if (Math.hypot(e.clientX - sx, e.clientY - sy) >= 4) {
            pending = false;
            sendToHost('window.dragStart');
          }
        });

        const clear = () => { pending = false; setPointerHold(false); };
        appRoot.addEventListener('pointerup', clear);
        appRoot.addEventListener('pointercancel', clear);
        window.addEventListener('pointerup', () => setPointerHold(false));
        window.addEventListener('pointercancel', () => setPointerHold(false));
        window.addEventListener('blur', () => setPointerHold(false));
      })();

      /* Перетаскивание треков и плейлистов (Drag-and-Drop во всех вкладках) */
      (function initDragAndDrop() {
        let dragState = null;

        function clearDragClasses() {
          appRoot.querySelectorAll('.is-dragging, .is-drag-over-top, .is-drag-over-bottom').forEach(el => {
            el.classList.remove('is-dragging', 'is-drag-over-top', 'is-drag-over-bottom');
          });
        }

        function isValidTarget(target) {
          if (!dragState || !target || target === dragState.el) return false;
          const tKind = target.getAttribute('data-drag-kind');
          if (tKind !== dragState.kind) return false;
          if (tKind === 'playlist-track') {
            return target.getAttribute('data-playlist-id') === dragState.playlistId;
          }
          if (tKind === 'playlists') {
            return target.getAttribute('data-drag-pinned') === dragState.pinned;
          }
          return true;
        }

        appRoot.addEventListener('dragstart', (e) => {
          if (e.target.closest('button, input, .ow-var-chip')) {
            e.preventDefault();
            return;
          }
          const row = e.target.closest('[data-drag-kind]');
          if (!row) return;
          dragState = {
            el: row,
            kind: row.getAttribute('data-drag-kind'),
            key: row.getAttribute('data-drag-key') || '',
            index: Number(row.getAttribute('data-drag-index') || -1),
            playlistId: row.getAttribute('data-playlist-id') || '',
            pinned: row.getAttribute('data-drag-pinned') || 'false'
          };
          row.classList.add('is-dragging');
          if (e.dataTransfer) {
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', dragState.key || String(dragState.index));
          }
        });

        appRoot.addEventListener('dragover', (e) => {
          const target = e.target.closest('[data-drag-kind]');
          if (!isValidTarget(target)) return;
          e.preventDefault();
          if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';

          appRoot.querySelectorAll('.is-drag-over-top, .is-drag-over-bottom').forEach(el => {
            if (el !== target) el.classList.remove('is-drag-over-top', 'is-drag-over-bottom');
          });
          const rect = target.getBoundingClientRect();
          const before = (e.clientY - rect.top) < rect.height * 0.5;
          target.classList.toggle('is-drag-over-top', before);
          target.classList.toggle('is-drag-over-bottom', !before);
        });

        appRoot.addEventListener('dragleave', (e) => {
          const target = e.target.closest('[data-drag-kind]');
          if (target && !target.contains(e.relatedTarget)) {
            target.classList.remove('is-drag-over-top', 'is-drag-over-bottom');
          }
        });

        appRoot.addEventListener('drop', (e) => {
          const target = e.target.closest('[data-drag-kind]');
          if (!isValidTarget(target)) {
            clearDragClasses();
            dragState = null;
            return;
          }
          e.preventDefault();

          const kind = dragState.kind;
          const srcKey = dragState.key;
          const srcIdx = dragState.index;
          const dstKey = target.getAttribute('data-drag-key') || '';
          const dstIdx = Number(target.getAttribute('data-drag-index') || -1);
          const plId = dragState.playlistId;

          clearDragClasses();
          dragState = null;

          if (kind === 'queue' && srcIdx >= 0 && dstIdx >= 0 && srcIdx !== dstIdx) {
            const moved = state.queue.splice(srcIdx, 1)[0];
            state.queue.splice(dstIdx, 0, moved);
            refreshTabBodyOnly();
            sendToHost('queue.move', { from: srcIdx, to: dstIdx });
          } else if (kind === 'search' && srcKey && dstKey && srcKey !== dstKey) {
            const sI = state.searchResults.findIndex(x => (x.groupKey || x.uniqueKey) === srcKey);
            const dI = state.searchResults.findIndex(x => (x.groupKey || x.uniqueKey) === dstKey);
            if (sI >= 0 && dI >= 0 && sI !== dI) {
              const moved = state.searchResults.splice(sI, 1)[0];
              state.searchResults.splice(dI, 0, moved);
              refreshTabBodyOnly();
              sendToHost('search.reorder', { sourceKey: srcKey, targetKey: dstKey });
            }
          } else if (kind === 'favorites' && srcKey && dstKey && srcKey !== dstKey) {
            const sI = state.favorites.findIndex(x => x.uniqueKey === srcKey);
            const dI = state.favorites.findIndex(x => x.uniqueKey === dstKey);
            if (sI >= 0 && dI >= 0 && sI !== dI) {
              const moved = state.favorites.splice(sI, 1)[0];
              state.favorites.splice(dI, 0, moved);
              refreshTabBodyOnly();
              sendToHost('favorites.reorder', { sourceKey: srcKey, targetKey: dstKey });
            }
          } else if (kind === 'playlists' && srcKey && dstKey && srcKey !== dstKey) {
            const sI = state.playlists.findIndex(x => x.id === srcKey);
            const dI = state.playlists.findIndex(x => x.id === dstKey);
            if (sI >= 0 && dI >= 0 && sI !== dI) {
              const moved = state.playlists.splice(sI, 1)[0];
              state.playlists.splice(dI, 0, moved);
              refreshTabBodyOnly();
              sendToHost('playlists.reorder', { sourceId: srcKey, targetId: dstKey });
            }
          } else if (kind === 'playlist-track' && plId && srcKey && dstKey && srcKey !== dstKey) {
            const pl = state.playlists.find(x => x.id === plId);
            if (pl && Array.isArray(pl.tracks)) {
              const sI = pl.tracks.findIndex(x => x.uniqueKey === srcKey);
              const dI = pl.tracks.findIndex(x => x.uniqueKey === dstKey);
              if (sI >= 0 && dI >= 0 && sI !== dI) {
                const moved = pl.tracks.splice(sI, 1)[0];
                pl.tracks.splice(dI, 0, moved);
                refreshTabBodyOnly();
                sendToHost('playlists.reorderTrack', { playlistId: plId, sourceKey: srcKey, targetKey: dstKey });
              }
            }
          }
        });

        appRoot.addEventListener('dragend', () => {
          clearDragClasses();
          dragState = null;
        });
      })();

      /* Глобальные локальные горячие клавиши: Space, ←/→, Shift+←/→, /, M */
      window.addEventListener('keydown', (e) => {
        const tag = e.target && e.target.tagName ? e.target.tagName.toLowerCase() : '';
        const typing = tag === 'input' || tag === 'textarea' || tag === 'select';

        if (e.key === 'Escape' && typing) {
          e.target.blur();
          return;
        }
        if (typing) return;

        if (e.code === 'Space' && !e.ctrlKey && !e.altKey) {
          e.preventDefault();
          sendToHost('player.togglePause');
        } else if (e.key === 'ArrowRight' && e.shiftKey) {
          e.preventDefault();
          sendToHost('player.seekRelative', { deltaMs: 15000 });
        } else if (e.key === 'ArrowLeft' && e.shiftKey) {
          e.preventDefault();
          sendToHost('player.seekRelative', { deltaMs: -15000 });
        } else if (e.key === 'ArrowRight') {
          e.preventDefault();
          sendToHost('player.next');
        } else if (e.key === 'ArrowLeft') {
          e.preventDefault();
          sendToHost('player.prev');
        } else if ((e.key === '/' || (e.code === 'Slash' && !e.shiftKey)) && !e.ctrlKey && !e.altKey) {
          e.preventDefault();
          state.activeTab = 'search';
          sendToHost('ui.tabChanged', { tab: 'search' });
          if (state.compact) {
            state.pendingFocusSearch = true;
            sendToHost('window.setCompactMode', { compact: false });
          } else {
            state.pendingFocusSearch = true;
            renderApp();
          }
        } else if ((e.code === 'KeyM' || e.key === 'm' || e.key === 'M' || e.key === 'ь' || e.key === 'Ь') && !e.ctrlKey && !e.altKey) {
          e.preventDefault();
          sendToHost('window.setCompactMode', { compact: !state.compact });
        }
      });

      function applyInitOrResume(msg, isInit) {
        if (isInit && typeof msg.compact === 'boolean') state.compact = msg.compact;
        if (Array.isArray(msg.favorites)) state.favorites = msg.favorites;
        if (Array.isArray(msg.playlists)) state.playlists = msg.playlists;
        if (Array.isArray(msg.queue)) state.queue = msg.queue;
        if ('currentTrack' in msg) state.currentTrack = msg.currentTrack;
        if ('coverDataUrl' in msg) state.coverDataUrl = msg.coverDataUrl;
        if (typeof msg.isPlaying === 'boolean') state.isPlaying = msg.isPlaying;
        if (typeof msg.currentMs === 'number') state.currentMs = msg.currentMs;
        if (typeof msg.totalMs === 'number') state.totalMs = msg.totalMs;
        if (typeof msg.volume === 'number') state.volume = msg.volume;
        if (typeof msg.repeatMode === 'number') state.repeatMode = msg.repeatMode;
        if (typeof msg.shuffle === 'boolean') state.shuffle = msg.shuffle;
        if ('externalSource' in msg) state.externalSource = msg.externalSource;
        if (msg.windowSettings) state.windowSettings = Object.assign({}, state.windowSettings, msg.windowSettings);
        if (msg.telemetry) state.telemetry = msg.telemetry;
        if (msg.eq) state.eq = msg.eq;
        if (msg.proxy && typeof msg.proxy === 'object') {
          state.proxy = Object.assign({}, state.proxy, msg.proxy);
          syncProxyDraftFromServer(isInit);
        }
        if (isInit || 'yandexConnected' in msg) {
          state.accounts = {
            yandexConnected: Boolean(msg.yandexConnected),
            yandexUserName: msg.yandexUserName || '',
            vkConnected: Boolean(msg.vkConnected),
            vkUserName: msg.vkUserName || '',
            spotifyConnected: Boolean(msg.spotifyConnected),
            spotifyUserName: msg.spotifyUserName || '',
            youtubeCookiesConnected: Boolean(msg.youtubeCookiesConnected),
            proxyActive: Boolean(msg.proxyActive)
          };
        } else {
          if ('youtubeCookiesConnected' in msg) state.accounts.youtubeCookiesConnected = Boolean(msg.youtubeCookiesConnected);
          if ('proxyActive' in msg) state.accounts.proxyActive = Boolean(msg.proxyActive);
        }
        ChromaEngine.updateForTrack(state.currentTrack, state.coverDataUrl);
        renderApp();
      }

      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', (ev) => {
          try {
            const msg = typeof ev.data === 'string' ? JSON.parse(ev.data) : ev.data;
            if (!msg || !msg.type) return;

            switch (msg.type) {
              case 'initState':
                applyInitOrResume(msg, true);
                break;
              case 'resumeSnapshot':
                applyInitOrResume(msg, false);
                break;
              case 'uiVisibility':
                state.suspended = !msg.visible;
                appRoot.classList.toggle('ow-suspended', state.suspended);
                updatePlaybackButtonsUI();
                break;
              case 'windowMode':
                state.compact = Boolean(msg.compact);
                state.windowSettings.compact = state.compact;
                renderApp();
                break;
              case 'windowSettings':
                Object.assign(state.windowSettings, msg);
                ChromaEngine.syncStrength();
                if (appRoot) {
                  appRoot.setAttribute('data-edge', state.windowSettings.dockedEdge || '');
                  appRoot.setAttribute('data-edge-collapsed', state.windowSettings.edgeCollapsed ? 'true' : 'false');
                }
                if (state.compact) {
                  const dockBtn = appRoot.querySelector('[data-role="dock-edge-btn"]');
                  if (dockBtn) dockBtn.classList.toggle('is-active', Boolean(state.windowSettings.dockedEdge));
                } else {
                  const pinBtn = appRoot.querySelector('[data-role="pin-btn"]');
                  if (pinBtn) pinBtn.classList.toggle('is-active', Boolean(state.windowSettings.alwaysOnTop));
                  if (state.activeTab === 'settings' && !isTypingInSettings()) refreshTabBodyOnly();
                }
                break;
              case 'edgeDockState':
                state.windowSettings.dockedEdge = msg.dockedEdge || '';
                state.windowSettings.edgeCollapsed = Boolean(msg.collapsed);
                if (appRoot) {
                  appRoot.setAttribute('data-edge', state.windowSettings.dockedEdge);
                  appRoot.setAttribute('data-edge-collapsed', state.windowSettings.edgeCollapsed ? 'true' : 'false');
                }
                {
                  const dockBtn = appRoot.querySelector('[data-role="dock-edge-btn"]');
                  if (dockBtn) dockBtn.classList.toggle('is-active', Boolean(state.windowSettings.dockedEdge));
                }
                break;
              case 'accountsUpdate':
                state.accounts = {
                  yandexConnected: Boolean(msg.yandexConnected),
                  yandexUserName: msg.yandexUserName || '',
                  vkConnected: Boolean(msg.vkConnected),
                  vkUserName: msg.vkUserName || '',
                  spotifyConnected: Boolean(msg.spotifyConnected),
                  spotifyUserName: msg.spotifyUserName || '',
                  youtubeCookiesConnected: Boolean(msg.youtubeCookiesConnected),
                  proxyActive: Boolean(msg.proxyActive)
                };
                if (!state.compact) {
                  const bDot = appRoot.querySelector('[data-role="bridge-dot"]');
                  const bm = getBridgeMeta();
                  if (bDot) {
                    bDot.style.background = bm.color;
                    bDot.title = bm.label;
                  }
                  if (state.activeTab === 'settings' && !isTypingInSettings()) refreshTabBodyOnly();
                }
                break;
              case 'trackChanged':
                state.currentTrack = msg.track || null;
                state.coverDataUrl = msg.coverDataUrl || null;
                state.totalMs = (state.currentTrack && state.currentTrack.durationMs) || state.totalMs;
                ChromaEngine.updateForTrack(state.currentTrack, state.coverDataUrl);
                updateHeroMetaUI();
                updateTimeUI();
                if (!state.compact && (state.activeTab === 'search' || state.activeTab === 'queue' || state.activeTab === 'favorites')) {
                  refreshTabBodyOnly();
                }
                break;
              case 'position':
                state.currentMs = msg.currentMs || 0;
                if (msg.totalMs > 0) state.totalMs = msg.totalMs;
                updateTimeUI();
                break;
              case 'playbackState':
                state.isPlaying = Boolean(msg.isPlaying);
                updatePlaybackButtonsUI();
                break;
              case 'shuffleState':
                state.shuffle = Boolean(msg.enabled);
                updatePlaybackButtonsUI();
                break;
              case 'repeatMode':
                state.repeatMode = Number(msg.mode || 0);
                updatePlaybackButtonsUI();
                break;
              case 'volume':
                state.volume = Number(msg.volume ?? state.volume);
                updateVolumeUI();
                break;
              case 'searchResults':
                state.searchResults = Array.isArray(msg.items) ? msg.items : [];
                state.searchStatus = msg.status || '';
                state.canLoadMore = Boolean(msg.canLoadMore);
                state.searchLoading = Boolean(msg.isLoading);
                if (!state.compact && state.activeTab === 'search') {
                  refreshTabBodyOnly();
                }
                break;
              case 'queueUpdate':
                state.queue = Array.isArray(msg.items) ? msg.items : [];
                updateTabBadges();
                if (!state.compact && state.activeTab === 'queue') {
                  refreshTabBodyOnly();
                }
                break;
              case 'libraryUpdate':
                state.favorites = Array.isArray(msg.favorites) ? msg.favorites : [];
                state.playlists = Array.isArray(msg.playlists) ? msg.playlists : [];
                if (state.currentTrack && typeof msg.currentTrackLiked === 'boolean') {
                  state.currentTrack.isLiked = msg.currentTrackLiked;
                }
                {
                  const favKeys = new Set(state.favorites.map(f => f && f.uniqueKey).filter(Boolean));
                  const syncLiked = (arr) => {
                    if (!Array.isArray(arr)) return;
                    for (const item of arr) {
                      if (!item) continue;
                      if (item.uniqueKey) item.isLiked = favKeys.has(item.uniqueKey);
                      if (Array.isArray(item.variants)) {
                        for (const v of item.variants) {
                          if (v && v.uniqueKey) v.isLiked = favKeys.has(v.uniqueKey);
                        }
                      }
                    }
                  };
                  syncLiked(state.searchResults);
                  syncLiked(state.queue);
                }
                updateHeroMetaUI();
                updateTabBadges();
                if (!state.compact && (state.activeTab === 'favorites' || state.activeTab === 'search' || state.activeTab === 'queue')) {
                  refreshTabBodyOnly();
                }
                break;
              case 'eqState':
                state.eq = Object.assign({}, state.eq, msg);
                if (!state.compact && state.activeTab === 'settings') {
                  refreshTabBodyOnly();
                }
                break;
              case 'trackProgress':
                if (msg.uniqueKey) {
                  const applyPatch = (target) => {
                    if (!target) return;
                    target.isDownloading = Boolean(msg.isDownloading);
                    target.downloadProgress = msg.downloadProgress || 0;
                    target.isDownloaded = Boolean(msg.isDownloaded);
                  };
                  const patch = (arr) => {
                    if (!Array.isArray(arr)) return;
                    for (const item of arr) {
                      if (!item) continue;
                      if (item.uniqueKey === msg.uniqueKey) applyPatch(item);
                      if (Array.isArray(item.variants)) {
                        for (const v of item.variants) {
                          if (v && v.uniqueKey === msg.uniqueKey) applyPatch(v);
                        }
                      }
                    }
                  };
                  patch(state.searchResults);
                  patch(state.favorites);
                  patch(state.queue);
                  if (!state.compact && (state.activeTab === 'search' || state.activeTab === 'favorites')) {
                    refreshTabBodyOnly();
                  }
                }
                break;
              case 'proxyStatus':
                state.proxy = Object.assign({}, state.proxy, {
                  text: msg.text || '',
                  kind: msg.kind || '',
                  testing: Boolean(msg.testing)
                });
                if ('enabled' in msg) state.proxy.enabled = Boolean(msg.enabled);
                if ('type' in msg && msg.type !== undefined) state.proxy.type = msg.type;
                if ('host' in msg && msg.host !== undefined) state.proxy.host = msg.host;
                if ('port' in msg && msg.port !== undefined) state.proxy.port = msg.port;
                if ('username' in msg && msg.username !== undefined) state.proxy.username = msg.username;
                if ('password' in msg && msg.password !== undefined) state.proxy.password = msg.password;
                if ('bypassRu' in msg) state.proxy.bypassRu = Boolean(msg.bypassRu);
                if (msg.kind !== 'error' && !msg.testing && msg.host !== undefined) {
                  syncProxyDraftFromServer(true);
                }
                if (!state.compact && state.activeTab === 'settings') {
                  if (!isTypingInSettings() && msg.kind !== 'error' && !msg.testing && msg.host !== undefined) {
                    refreshTabBodyOnly();
                  } else {
                    const pSt = appRoot.querySelector('[data-role="proxy-status"]');
                    if (pSt) {
                      pSt.textContent = state.proxy.text || '';
                      pSt.className = 'ow-status-line ' + statusKindClass(msg.kind);
                    }
                    const pBtn = appRoot.querySelector('[data-action="proxy-test"]');
                    if (pBtn) {
                      pBtn.disabled = Boolean(msg.testing);
                      pBtn.textContent = msg.testing ? 'Проверка...' : 'Проверить';
                    }
                  }
                }
                break;
              case 'ytDlpStatus':
                state.ytDlp = Object.assign({}, state.ytDlp, msg);
                if (!state.compact && state.activeTab === 'settings') {
                  const ySt = appRoot.querySelector('[data-role="ytdlp-status"]');
                  if (ySt) {
                    ySt.textContent = msg.text || '';
                    ySt.className = 'ow-status-line ' + (msg.ok ? 'is-ok' : '') + ' truncate';
                  }
                  const yBtn = appRoot.querySelector('[data-action="check-ytdlp"]');
                  if (yBtn) {
                    yBtn.disabled = Boolean(msg.busy);
                    yBtn.textContent = msg.busy ? '...' : 'Проверить';
                  }
                }
                break;
              case 'updateStatus':
                {
                  const prevAvail = Boolean(state.update && state.update.isAvailable);
                  state.update = Object.assign({}, state.update, msg);
                  if (!state.compact && state.activeTab === 'settings') {
                    if (!isTypingInSettings() && prevAvail !== Boolean(msg.isAvailable)) {
                      refreshTabBodyOnly();
                    } else {
                      const uSt = appRoot.querySelector('[data-role="update-status"]');
                      if (uSt) {
                        uSt.textContent = msg.text || '';
                        uSt.className = 'ow-status-line ' + (msg.isAvailable ? 'is-warn' : (msg.checkedOk ? 'is-ok' : ''));
                      }
                      const uProg = appRoot.querySelector('[data-role="update-progress"]');
                      if (uProg) {
                        const pct = Math.max(0, Math.min(100, Number(msg.progress) || 0));
                        uProg.style.display = (msg.busy && pct > 0) ? 'block' : 'none';
                        const uFill = uProg.querySelector('.ow-progress-fill');
                        if (uFill) uFill.style.width = pct + '%';
                      }
                      const uChk = appRoot.querySelector('[data-action="check-update"]');
                      if (uChk) {
                        uChk.disabled = Boolean(msg.busy);
                        uChk.textContent = 'Проверить';
                      }
                      const uInst = appRoot.querySelector('[data-action="install-update"]');
                      if (uInst) {
                        uInst.disabled = Boolean(msg.busy) || !msg.isAvailable;
                        uInst.style.cssText = msg.isAvailable ? 'border-color:rgba(var(--chroma-r),var(--chroma-g),var(--chroma-b),0.55);color:#FFFFFF' : 'opacity:0.45';
                      }
                    }
                  }
                }
                break;
              case 'authPending':
                {
                  const nextProv = msg.active === false ? '' : (msg.provider || '');
                  const sameProv = nextProv && state.authPanel.provider === nextProv;
                  state.authPanel = Object.assign({}, state.authPanel, {
                    provider: nextProv,
                    busy: Boolean(msg.busy),
                    mode: msg.active === false ? '' : (msg.mode !== undefined ? (msg.mode || '') : (sameProv ? (state.authPanel.mode || '') : '')),
                    userCode: msg.active === false ? '' : (msg.userCode !== undefined ? (msg.userCode || '') : (sameProv ? (state.authPanel.userCode || '') : '')),
                    verificationUrl: msg.active === false ? '' : (msg.verificationUrl !== undefined ? (msg.verificationUrl || '') : (sameProv ? (state.authPanel.verificationUrl || '') : ''))
                  });
                  if (!state.compact && state.activeTab === 'settings') {
                    if (!isTypingInSettings() || msg.active === false) {
                      refreshTabBodyOnly();
                      if (msg.active !== false) scrollElementIntoViewSoon('.ow-inline-auth');
                    } else {
                      const aBtn = appRoot.querySelector('[data-action="auth-submit"]');
                      if (aBtn) {
                        aBtn.disabled = Boolean(msg.busy);
                        aBtn.textContent = msg.busy ? 'Проверка...' : 'Подключить';
                      }
                    }
                  }
                }
                break;
              case 'authResult':
                state.authPanel.busy = false;
                state.authPanel.statusText = msg.message || '';
                state.authPanel.statusOk = Boolean(msg.ok);
                if (msg.ok && state.authPanel.provider === msg.provider) {
                  state.authPanel.provider = '';
                  state.authPanel.mode = '';
                  state.authPanel.userCode = '';
                  state.authPanel.verificationUrl = '';
                  state.authPanel.input = '';
                }
                if (!state.compact && state.activeTab === 'settings') {
                  if (msg.ok || !isTypingInSettings()) {
                    refreshTabBodyOnly();
                    if (!msg.ok && state.authPanel.provider) scrollElementIntoViewSoon('.ow-inline-auth');
                  } else {
                    const aBtn = appRoot.querySelector('[data-action="auth-submit"]');
                    if (aBtn) {
                      aBtn.disabled = false;
                      aBtn.textContent = 'Подключить';
                    }
                    const authBox = appRoot.querySelector('.ow-inline-auth');
                    if (authBox) {
                      let aSt = authBox.querySelector('.ow-status-line');
                      if (!aSt && state.authPanel.statusText) {
                        aSt = document.createElement('div');
                        authBox.appendChild(aSt);
                      }
                      if (aSt) {
                        aSt.className = 'ow-status-line is-err';
                        aSt.textContent = state.authPanel.statusText;
                      }
                    }
                  }
                }
                break;
              case 'showTab':
                if (msg.tab) {
                  state.activeTab = String(msg.tab);
                  if (msg.section) state.pendingScrollSection = String(msg.section);
                  sendToHost('ui.tabChanged', { tab: state.activeTab });
                  if (!state.compact) renderApp();
                }
                break;
              case 'focusNewPlaylist':
                state.activeTab = 'favorites';
                state.favSubTab = 'playlists';
                state.openedPlaylistId = null;
                state.pendingFocusNewPlaylist = true;
                sendToHost('ui.tabChanged', { tab: 'favorites' });
                if (state.compact) {
                  sendToHost('window.setCompactMode', { compact: false });
                } else {
                  renderApp();
                }
                break;
              case 'telemetry':
                state.telemetry = msg;
                const tEl = appRoot.querySelector('[data-role="telemetry-text"]');
                if (tEl) tEl.textContent = formatTelemetryText();
                break;
              case 'status':
                showToast(msg.message);
                break;
            }
          } catch (e) {}
        });
      }

      renderApp();
      sendToHost('ready', {});
    })();
  </script>
</body>
</html>
""";

    public static string GetHtml(bool nativeWin11Corners)
        => nativeWin11Corners
            ? Html.Replace("<html lang=\"ru\">", "<html lang=\"ru\" class=\"ow-win11\">", StringComparison.Ordinal)
            : Html;
}
