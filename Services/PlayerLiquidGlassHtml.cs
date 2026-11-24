namespace DynamicIsland.Services;

public static class PlayerLiquidGlassHtml
{
    public const string Html = """
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width, initial-scale=1.0"/>
<title>OmniWave</title>
<style>
  :root {
    --accent: #FFCC00;
    --accent-rgb: 255, 204, 0;
    --orb2-rgb: 56, 189, 248;
    --orb3-rgb: 168, 85, 247;
    --spring: cubic-bezier(0.22, 1.28, 0.36, 1);
    --ease-out: cubic-bezier(0.22, 1, 0.36, 1);
    --glass-border: rgba(255, 255, 255, 0.24);
    --glass-top-edge: rgba(255, 255, 255, 0.56);
    --glass-bg: rgba(255, 255, 255, 0.085);
    --glass-bg-hover: rgba(255, 255, 255, 0.16);
  }

  *, *::before, *::after {
    box-sizing: border-box;
    margin: 0;
    padding: 0;
    user-select: none;
    -webkit-font-smoothing: antialiased;
  }

  html, body {
    width: 100vw;
    height: 100vh;
    overflow: hidden;
    background: transparent;
    color: #ffffff;
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
  }

  /* Clear Glass window shell over real Windows 11 Dark Acrylic */
  .app-shell {
    position: relative;
    width: 100vw;
    height: 100vh;
    display: flex;
    flex-direction: row;
    overflow: hidden;
    background: linear-gradient(
      145deg,
      rgba(14, 18, 28, 0.36) 0%,
      rgba(9, 12, 20, 0.44) 55%,
      rgba(12, 15, 25, 0.38) 100%
    );
    box-shadow:
      inset 0 1px 0 rgba(255, 255, 255, 0.38),
      inset 0 -1px 0 rgba(255, 255, 255, 0.08),
      inset 1px 0 0 rgba(255, 255, 255, 0.16),
      inset -1px 0 0 rgba(255, 255, 255, 0.16);
  }

  /* Specular top sheen + optical rim */
  .glass-surface {
    position: relative;
    background: var(--glass-bg);
    backdrop-filter: blur(32px) saturate(215%) brightness(1.08);
    -webkit-backdrop-filter: blur(32px) saturate(215%) brightness(1.08);
    border: 1px solid var(--glass-border);
    box-shadow:
      inset 0 1px 0.6px var(--glass-top-edge),
      inset 0 -1px 1px rgba(255, 255, 255, 0.12),
      inset 1px 0 0.6px rgba(255, 255, 255, 0.22),
      0 12px 32px rgba(0, 0, 0, 0.32);
  }

  .glass-glare {
    position: relative;
    overflow: hidden;
  }

  .glass-glare::after {
    content: '';
    position: absolute;
    inset: 0;
    border-radius: inherit;
    pointer-events: none;
    background: radial-gradient(
      130px circle at var(--mx, 50%) var(--my, 15%),
      rgba(255, 255, 255, 0.28),
      rgba(255, 255, 255, 0.04) 45%,
      transparent 70%
    );
    opacity: 0;
    transition: opacity 0.24s ease;
  }

  .glass-glare:hover::after {
    opacity: 1;
  }

  /* Custom slim glass scrollbar */
  .glass-scroll::-webkit-scrollbar {
    width: 6px;
    height: 6px;
  }
  .glass-scroll::-webkit-scrollbar-track {
    background: transparent;
  }
  .glass-scroll::-webkit-scrollbar-thumb {
    background: rgba(255, 255, 255, 0.18);
    border-radius: 999px;
  }
  .glass-scroll::-webkit-scrollbar-thumb:hover {
    background: rgba(var(--accent-rgb), 0.78);
  }

  /* ==================== LEFT CLEAR GLASS SIDEBAR ==================== */
  #leftSidebar {
    width: 340px;
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    min-height: 0;
    overflow: hidden;
    background: linear-gradient(180deg, rgba(255, 255, 255, 0.09) 0%, rgba(12, 16, 26, 0.34) 100%);
    backdrop-filter: blur(36px) saturate(200%);
    -webkit-backdrop-filter: blur(36px) saturate(200%);
    border-right: 1px solid rgba(255, 255, 255, 0.18);
    box-shadow: inset -1px 0 0 rgba(255, 255, 255, 0.08);
    z-index: 15;
    transition: width 0.28s var(--ease-out), opacity 0.22s ease, border-right-width 0.28s ease;
  }

  #leftSidebar.collapsed {
    width: 0;
    border-right-width: 0;
    opacity: 0;
    pointer-events: none;
  }

  .sidebar-inner {
    width: 340px;
    height: 100%;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }

  @media (max-width: 900px) {
    #leftSidebar {
      position: absolute;
      left: 0;
      top: 0;
      bottom: 0;
      background: rgba(12, 16, 26, 0.78);
      box-shadow: 24px 0 60px rgba(0, 0, 0, 0.68);
    }
  }

  .sidebar-header {
    flex-shrink: 0;
    padding: 14px 14px 10px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 6px;
  }

  .brand-logo {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 16px;
    font-weight: 800;
    letter-spacing: -0.02em;
    color: var(--accent);
    text-shadow: 0 0 18px rgba(var(--accent-rgb), 0.38);
    white-space: nowrap;
  }

  .acc-pill {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    padding: 4px 9px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.2);
    background: rgba(255, 255, 255, 0.09);
    color: rgba(255, 255, 255, 0.88);
    font-family: inherit;
    font-size: 10.5px;
    font-weight: 600;
    white-space: nowrap;
    cursor: pointer;
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.35);
    transition: transform 0.2s var(--spring), background 0.16s ease;
  }
  .acc-pill:hover { background: rgba(255, 255, 255, 0.18); transform: scale(1.03); }
  .acc-pill:active { transform: scale(0.94); }

  .acc-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: rgba(255, 255, 255, 0.28);
    transition: background 0.25s ease, box-shadow 0.25s ease;
  }
  .acc-dot.yandex.on { background: #FFCC00; box-shadow: 0 0 8px #FFCC00; }
  .acc-dot.vk.on { background: #0077FF; box-shadow: 0 0 8px #0077FF; }

  /* 3-tab Liquid Glass capsule with sliding indicator */
  .tab-capsule {
    position: relative;
    flex-shrink: 0;
    margin: 0 14px 10px;
    padding: 4px;
    border-radius: 14px;
    background: rgba(255, 255, 255, 0.07);
    border: 1px solid rgba(255, 255, 255, 0.16);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.22);
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 2px;
  }

  .tab-slider {
    position: absolute;
    top: 4px;
    bottom: 4px;
    left: 4px;
    width: calc((100% - 12px) / 3);
    border-radius: 10px;
    background: rgba(255, 255, 255, 0.18);
    border: 1px solid rgba(255, 255, 255, 0.28);
    box-shadow:
      inset 0 1px 0 rgba(255, 255, 255, 0.45),
      0 4px 14px rgba(0, 0, 0, 0.22);
    transition: transform 0.34s var(--spring);
    pointer-events: none;
  }

  .tab-btn {
    position: relative;
    z-index: 1;
    padding: 6px 8px;
    border-radius: 10px;
    border: none;
    background: transparent;
    color: rgba(255, 255, 255, 0.65);
    font-family: inherit;
    font-size: 12px;
    font-weight: 600;
    cursor: pointer;
    transition: color 0.18s ease;
  }
  .tab-btn:hover { color: rgba(255, 255, 255, 0.92); }
  .tab-btn.active { color: #ffffff; font-weight: 700; }

  .search-input-wrap {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 11px;
    border-radius: 13px;
    background: rgba(255, 255, 255, 0.09);
    border: 1px solid rgba(255, 255, 255, 0.2);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.32);
    transition: border-color 0.2s ease, background 0.2s ease, box-shadow 0.2s ease;
  }
  .search-input-wrap:focus-within {
    background: rgba(255, 255, 255, 0.13);
    border-color: rgba(var(--accent-rgb), 0.65);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.45), 0 0 18px rgba(var(--accent-rgb), 0.18);
  }

  .filter-pills-wrap {
    flex-shrink: 0;
    padding: 0 14px 6px;
    display: flex;
    flex-wrap: wrap;
    gap: 5px;
  }

  .filter-pill {
    padding: 4px 9px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.16);
    background: rgba(255, 255, 255, 0.08);
    color: rgba(255, 255, 255, 0.8);
    font-family: inherit;
    font-size: 11px;
    font-weight: 700;
    white-space: nowrap;
    cursor: pointer;
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.22);
    transition: transform 0.2s var(--spring), background 0.16s ease, color 0.16s ease, border-color 0.16s ease;
  }
  .filter-pill:hover:not(:disabled) {
    background: rgba(255, 255, 255, 0.18);
    color: #fff;
    transform: translateY(-1px);
  }
  .filter-pill:active:not(:disabled) { transform: scale(0.93); }
  .filter-pill:disabled { opacity: 0.35; cursor: default; }

  /* Track Cards */
  .track-card {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 7px 8px;
    border-radius: 14px;
    background: rgba(255, 255, 255, 0.045);
    border: 1px solid rgba(255, 255, 255, 0.06);
    cursor: pointer;
    min-width: 0;
    transition: background 0.18s ease, border-color 0.18s ease, transform 0.2s var(--spring), box-shadow 0.2s ease;
  }
  .track-card:hover {
    background: rgba(255, 255, 255, 0.12);
    border-color: rgba(255, 255, 255, 0.2);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.25);
  }
  .track-card.active {
    background: rgba(var(--accent-rgb), 0.16);
    border-color: rgba(var(--accent-rgb), 0.45);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.35), 0 6px 20px rgba(var(--accent-rgb), 0.14);
  }

  .cover-mini-eq {
    position: absolute;
    inset: 0;
    border-radius: 9px;
    background: rgba(8, 10, 16, 0.56);
    backdrop-filter: blur(4px);
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 2px;
  }
  .cover-mini-eq span {
    width: 2.5px;
    height: 5px;
    border-radius: 2px;
    background: var(--accent);
    animation: miniBarBounce 0.65s ease-in-out infinite alternate;
  }
  .cover-mini-eq span:nth-child(2) { animation-delay: 0.16s; }
  .cover-mini-eq span:nth-child(3) { animation-delay: 0.34s; }
  .cover-mini-eq span:nth-child(4) { animation-delay: 0.10s; }

  @keyframes miniBarBounce {
    0% { height: 4px; }
    100% { height: 15px; }
  }

  .card-icon-btn {
    width: 25px;
    height: 25px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.14);
    background: rgba(255, 255, 255, 0.08);
    color: rgba(255, 255, 255, 0.85);
    font-family: inherit;
    font-size: 10px;
    font-weight: 700;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    flex-shrink: 0;
    transition: transform 0.18s var(--spring), background 0.15s ease, color 0.15s ease;
  }
  .card-icon-btn:hover { background: rgba(255, 255, 255, 0.22); color: #ffffff; transform: scale(1.08); }
  .card-icon-btn:active { transform: scale(0.9); }

  .playlist-row {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 9px 10px;
    border-radius: 14px;
    background: rgba(255, 255, 255, 0.06);
    border: 1px solid rgba(255, 255, 255, 0.14);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.2);
    cursor: pointer;
    min-width: 0;
    transition: all 0.2s var(--spring);
  }
  .playlist-row:hover {
    background: rgba(255, 255, 255, 0.14);
    border-color: rgba(255, 255, 255, 0.28);
    transform: translateY(-1px);
  }
  .playlist-row.pinned {
    border-color: rgba(255, 204, 0, 0.42);
    background: rgba(255, 204, 0, 0.08);
  }
  .playlist-row.drag-over {
    border-color: #FFCC00;
    box-shadow: 0 0 14px rgba(255, 204, 0, 0.28);
  }

  /* ==================== CENTRAL CLEAR GLASS STAGE ==================== */
  #centerStage {
    flex: 1;
    min-width: 0;
    min-height: 0;
    position: relative;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    overflow: hidden;
  }

  /* Vibrant ambient color orbs behind clear glass */
  .stage-orb {
    position: absolute;
    border-radius: 50%;
    filter: blur(90px);
    pointer-events: none;
    transition: background 0.9s ease;
    will-change: transform;
  }
  .stage-orb-1 {
    width: 460px;
    height: 460px;
    top: -4%;
    left: 12%;
    background: radial-gradient(circle, rgba(var(--accent-rgb), 0.38) 0%, transparent 72%);
    animation: orbFloat1 10s ease-in-out infinite alternate;
  }
  .stage-orb-2 {
    width: 420px;
    height: 420px;
    bottom: -6%;
    right: 8%;
    background: radial-gradient(circle, rgba(var(--orb2-rgb), 0.34) 0%, transparent 72%);
    animation: orbFloat2 12s ease-in-out infinite alternate;
  }
  .stage-orb-3 {
    width: 360px;
    height: 360px;
    top: 28%;
    right: 22%;
    background: radial-gradient(circle, rgba(var(--orb3-rgb), 0.30) 0%, transparent 72%);
    animation: orbFloat3 14s ease-in-out infinite alternate;
  }

  @keyframes orbFloat1 {
    0% { transform: translate3d(-32px, -18px, 0) scale(1); }
    100% { transform: translate3d(44px, 28px, 0) scale(1.12); }
  }
  @keyframes orbFloat2 {
    0% { transform: translate3d(36px, 22px, 0) scale(1.06); }
    100% { transform: translate3d(-40px, -24px, 0) scale(0.95); }
  }
  @keyframes orbFloat3 {
    0% { transform: translate3d(-18px, 28px, 0) scale(0.95); }
    100% { transform: translate3d(28px, -32px, 0) scale(1.09); }
  }

  .stage-top-bar {
    flex-shrink: 0;
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    padding: 14px 18px;
    z-index: 5;
    min-width: 0;
    cursor: grab;
  }
  .stage-top-bar:active { cursor: grabbing; }

  .top-pill-btn {
    padding: 6px 12px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.22);
    background: rgba(255, 255, 255, 0.10);
    backdrop-filter: blur(22px) saturate(180%);
    -webkit-backdrop-filter: blur(22px) saturate(180%);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.44), 0 6px 18px rgba(0, 0, 0, 0.22);
    color: rgba(255, 255, 255, 0.92);
    font-family: inherit;
    font-size: 11.5px;
    font-weight: 600;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    flex-shrink: 0;
    white-space: nowrap;
    cursor: pointer;
    transition: transform 0.2s var(--spring), background 0.16s ease, border-color 0.16s ease, color 0.16s ease;
  }
  .top-pill-btn:hover { background: rgba(255, 255, 255, 0.20); color: #ffffff; transform: translateY(-1px); }
  .top-pill-btn:active { transform: scale(0.93); }
  .top-pill-btn.active {
    background: rgba(var(--accent-rgb), 0.24);
    border-color: rgba(var(--accent-rgb), 0.65);
    color: var(--accent);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.5), 0 0 18px rgba(var(--accent-rgb), 0.22);
  }

  .top-win-btn {
    width: 28px;
    height: 28px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.22);
    background: rgba(255, 255, 255, 0.10);
    backdrop-filter: blur(20px);
    box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.42);
    color: rgba(255, 255, 255, 0.9);
    font-family: inherit;
    font-size: 11px;
    font-weight: 700;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    flex-shrink: 0;
    transition: transform 0.2s var(--spring), background 0.16s ease;
  }
  .top-win-btn:hover { background: rgba(255, 255, 255, 0.22); color: #ffffff; transform: scale(1.08); }
  .top-win-btn:active { transform: scale(0.90); }
  .top-win-btn.close-btn:hover { background: rgba(255, 75, 100, 0.35); border-color: rgba(255, 110, 130, 0.55); }

  .center-hero {
    flex: 1;
    min-height: 0;
    min-width: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: clamp(8px, 1.5vh, 15px);
    padding: 0 26px;
    z-index: 4;
    overflow: hidden;
  }

  @keyframes heroCoverBreathe {
    0%, 100% { transform: scale(1); }
    50% { transform: scale(1.028); }
  }

  #heroCoverBox {
    position: relative;
    width: clamp(114px, 18.5vh, 168px);
    height: clamp(114px, 18.5vh, 168px);
    border-radius: 22px;
    overflow: hidden;
    flex-shrink: 0;
    border: 1px solid rgba(255, 255, 255, 0.28);
    box-shadow:
      0 24px 54px rgba(0, 0, 0, 0.56),
      0 0 42px rgba(var(--accent-rgb), 0.34),
      inset 0 1px 0 rgba(255, 255, 255, 0.45);
    transition: box-shadow 0.5s ease;
  }
  #heroCoverBox.playing {
    animation: heroCoverBreathe 3.4s ease-in-out infinite;
  }
  .hero-seek-overlay {
    position: absolute;
    inset: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 2px;
    background: rgba(8, 12, 22, 0.58);
    backdrop-filter: blur(8px);
    -webkit-backdrop-filter: blur(8px);
    color: #ffffff;
    font-variant-numeric: tabular-nums;
    opacity: 0;
    transform: scale(0.92);
    pointer-events: none;
    z-index: 4;
    transition: opacity 0.16s ease, transform 0.22s var(--spring);
  }
  .hero-seek-overlay.show {
    opacity: 1;
    transform: scale(1);
  }
  .hero-seek-overlay-delta {
    font-size: 20px;
    font-weight: 900;
    color: var(--accent);
    text-shadow: 0 2px 14px rgba(var(--accent-rgb), 0.65);
  }
  .hero-seek-overlay-time {
    font-size: 12px;
    font-weight: 700;
    color: rgba(255, 255, 255, 0.9);
  }

  .hero-capsule-row {
    width: min(630px, 100%);
    display: flex;
    align-items: center;
    gap: 8px;
    flex-shrink: 0;
    min-width: 0;
  }

  .hero-circle-btn {
    width: 42px;
    height: 42px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.24);
    background: rgba(255, 255, 255, 0.11);
    backdrop-filter: blur(26px) saturate(200%);
    -webkit-backdrop-filter: blur(26px) saturate(200%);
    box-shadow:
      inset 0 1px 0 rgba(255, 255, 255, 0.52),
      inset 0 -1px 0 rgba(255, 255, 255, 0.12),
      0 10px 26px rgba(0, 0, 0, 0.28);
    color: rgba(255, 255, 255, 0.94);
    font-family: inherit;
    font-size: 13px;
    font-weight: 700;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    flex-shrink: 0;
    position: relative;
    transition: transform 0.22s var(--spring), background 0.16s ease, border-color 0.16s ease, color 0.16s ease;
  }
  .hero-circle-btn:hover { background: rgba(255, 255, 255, 0.22); color: #ffffff; transform: scale(1.07); }
  .hero-circle-btn:active { transform: scale(0.91); }

  .hero-capsule-wrap {
    flex: 1;
    min-width: 0;
    position: relative;
    display: flex;
    align-items: center;
  }

  .hero-seek-tooltip {
    position: absolute;
    top: -34px;
    left: 0;
    transform: translateX(-50%) scale(0.92);
    padding: 4px 10px;
    border-radius: 999px;
    background: rgba(14, 18, 30, 0.92);
    border: 1px solid rgba(var(--accent-rgb), 0.62);
    box-shadow: 0 8px 22px rgba(0, 0, 0, 0.55), inset 0 1px 0 rgba(255, 255, 255, 0.25);
    color: var(--accent);
    font-size: 11.5px;
    font-weight: 800;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
    pointer-events: none;
    opacity: 0;
    z-index: 20;
    transition: opacity 0.14s ease, transform 0.14s ease;
  }
  .hero-seek-tooltip.show {
    opacity: 1;
    transform: translateX(-50%) scale(1);
  }

  /* Liquid Glass Progress Capsule - ZERO rectangular overhang guaranteed via overflow: hidden + border-radius: 999px */
  .glass-progress-capsule {
    width: 100%;
    min-width: 0;
    height: 54px;
    border-radius: 999px;
    position: relative;
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 20px;
    background: rgba(255, 255, 255, 0.11);
    backdrop-filter: blur(34px) saturate(220%) brightness(1.08);
    -webkit-backdrop-filter: blur(34px) saturate(220%) brightness(1.08);
    border: 1px solid rgba(255, 255, 255, 0.28);
    box-shadow:
      inset 0 1px 0.8px rgba(255, 255, 255, 0.58),
      inset 0 -1px 1px rgba(255, 255, 255, 0.14),
      0 14px 36px rgba(0, 0, 0, 0.32);
    overflow: hidden;
    cursor: pointer;
    transition: border-color 0.2s ease, transform 0.22s var(--spring);
  }
  .glass-progress-capsule:hover {
    border-color: rgba(255, 255, 255, 0.48);
  }
  .glass-progress-capsule.seek-flash {
    border-color: rgba(var(--accent-rgb), 0.88);
    box-shadow:
      inset 0 1px 0.8px rgba(255, 255, 255, 0.68),
      0 0 26px rgba(var(--accent-rgb), 0.46),
      0 14px 36px rgba(0, 0, 0, 0.34);
    transform: scale(1.015);
  }

  .glass-hover-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: 0%;
    background: rgba(255, 255, 255, 0.09);
    pointer-events: none;
    opacity: 0;
    transition: opacity 0.14s ease;
  }

  .glass-progress-fill {
    position: absolute;
    inset: 0 auto 0 0;
    width: 0%;
    border-radius: 999px 0 0 999px;
    background: linear-gradient(90deg,
      rgba(255, 255, 255, 0.14) 0%,
      rgba(var(--accent-rgb), 0.32) 70%,
      rgba(var(--accent-rgb), 0.54) 100%
    );
    box-shadow: inset -2px 0 14px rgba(var(--accent-rgb), 0.38);
    pointer-events: none;
    transition: width 0.12s linear;
  }

  .glass-progress-rail {
    position: absolute;
    left: 16px;
    right: 16px;
    bottom: 5px;
    height: 3.5px;
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.14);
    pointer-events: none;
    overflow: hidden;
  }
  .glass-progress-rail-fill {
    height: 100%;
    width: 0%;
    border-radius: 999px;
    background: linear-gradient(90deg, rgba(255, 255, 255, 0.85), var(--accent));
    box-shadow: 0 0 10px rgba(var(--accent-rgb), 0.85);
    transition: width 0.12s linear;
  }

  .hero-play-btn {
    width: 58px;
    height: 58px;
    border-radius: 999px;
    border: 1px solid rgba(255, 255, 255, 0.42);
    background: linear-gradient(160deg, rgba(255, 255, 255, 0.26) 0%, rgba(255, 255, 255, 0.12) 100%);
    backdrop-filter: blur(28px) saturate(200%);
    -webkit-backdrop-filter: blur(28px) saturate(200%);
    color: #ffffff;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    flex-shrink: 0;
    box-shadow:
      inset 0 1.5px 1px rgba(255, 255, 255, 0.65),
      inset 0 -1px 1px rgba(255, 255, 255, 0.18),
      0 14px 36px rgba(0, 0, 0, 0.42);
    transition: transform 0.22s var(--spring), background 0.16s ease;
  }
  .hero-play-btn:hover { background: rgba(255, 255, 255, 0.30); transform: scale(1.08); }
  .hero-play-btn:active { transform: scale(0.91); }

  /* Volume slider */
  input[type="range"].h-slider {
    -webkit-appearance: none;
    appearance: none;
    height: 4px;
    border-radius: 999px;
    background: rgba(255, 255, 255, 0.20);
    outline: none;
    cursor: pointer;
  }
  input[type="range"].h-slider::-webkit-slider-thumb {
    -webkit-appearance: none;
    appearance: none;
    width: 13px;
    height: 13px;
    border-radius: 50%;
    background: #ffffff;
    border: 2px solid var(--accent);
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.45);
    cursor: pointer;
    transition: transform 0.18s var(--spring);
  }
  input[type="range"].h-slider::-webkit-slider-thumb:hover { transform: scale(1.18); }

  input[type="range"].eq-slider-v {
    writing-mode: vertical-lr;
    direction: rtl;
    -webkit-appearance: slider-vertical;
    appearance: none;
    width: 6px;
    height: 92px;
    background: rgba(255, 255, 255, 0.16);
    border-radius: 999px;
    outline: none;
    cursor: pointer;
  }

  /* ==================== RIGHT CLEAR GLASS DRAWER ==================== */
  #rightDrawer {
    width: 0;
    flex-shrink: 0;
    min-height: 0;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    background: linear-gradient(180deg, rgba(255, 255, 255, 0.08) 0%, rgba(12, 16, 26, 0.38) 100%);
    backdrop-filter: blur(36px) saturate(200%);
    -webkit-backdrop-filter: blur(36px) saturate(200%);
    border-left: 0 solid rgba(255, 255, 255, 0.18);
    z-index: 15;
    transition: width 0.28s var(--ease-out), border-left-width 0.28s ease;
  }
  #rightDrawer.open {
    width: 320px;
    border-left-width: 1px;
  }
  .drawer-inner {
    width: 320px;
    height: 100%;
    display: flex;
    flex-direction: column;
    min-height: 0;
  }
  @media (max-width: 1040px) {
    #rightDrawer {
      position: absolute;
      right: 0;
      top: 0;
      bottom: 0;
      background: rgba(12, 16, 26, 0.78);
      box-shadow: -24px 0 60px rgba(0, 0, 0, 0.68);
    }
  }

  /* ==================== FLOATING CLEAR GLASS CONTEXT MENUS ==================== */
  .floating-menu {
    position: fixed;
    min-width: 228px;
    padding: 6px;
    border-radius: 16px;
    background: rgba(18, 22, 34, 0.76);
    backdrop-filter: blur(34px) saturate(210%);
    -webkit-backdrop-filter: blur(34px) saturate(210%);
    border: 1px solid rgba(255, 255, 255, 0.24);
    box-shadow:
      inset 0 1px 0 rgba(255, 255, 255, 0.45),
      0 22px 54px rgba(0, 0, 0, 0.65);
    z-index: 2000;
    opacity: 0;
    transform: translateY(-6px) scale(0.95);
    pointer-events: none;
    transition: opacity 0.16s ease, transform 0.22s var(--spring);
  }
  .floating-menu.open {
    opacity: 1;
    transform: translateY(0) scale(1);
    pointer-events: auto;
  }

  .f-menu-item {
    width: 100%;
    padding: 8px 11px;
    border-radius: 10px;
    border: none;
    background: transparent;
    color: rgba(255, 255, 255, 0.94);
    font-family: inherit;
    font-size: 12.5px;
    font-weight: 500;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 10px;
    cursor: pointer;
    text-align: left;
    transition: background 0.14s ease;
  }
  .f-menu-item:hover { background: rgba(255, 255, 255, 0.14); color: #ffffff; }
  .f-menu-sep { height: 1px; margin: 5px 8px; background: rgba(255, 255, 255, 0.12); }

  /* Karaoke lines */
  .lyric-line {
    transition: opacity 0.25s ease, transform 0.25s var(--ease-out);
  }
</style>
</head>
<body>
<!-- Subtle SVG optical filter definition -->
<svg width="0" height="0" style="position:absolute;pointer-events:none;">
  <defs>
    <filter id="liquid-refract" x="-10%" y="-10%" width="120%" height="120%">
      <feTurbulence type="fractalNoise" baseFrequency="0.012" numOctaves="2" seed="7" result="noise"/>
      <feDisplacementMap in="SourceGraphic" in2="noise" scale="3" xChannelSelector="R" yChannelSelector="G"/>
    </filter>
  </defs>
</svg>

<div class="app-shell">
  <!-- LEFT CLEAR GLASS SIDEBAR -->
  <aside id="leftSidebar">
    <div class="sidebar-inner">
      <div class="sidebar-header">
        <div class="brand-logo">
          <span>✦</span>
          <span>OmniWave</span>
        </div>
        <div style="display:flex;align-items:center;gap:5px;">
          <button id="btnAccounts" class="acc-pill glass-glare" title="Аккаунты Яндекс Музыки и ВК">
            <span id="dotYandex" class="acc-dot yandex"></span>
            <span id="dotVk" class="acc-dot vk"></span>
            <span id="accPillLabel">Яндекс • ВК</span>
          </button>
          <button id="btnSettings" class="top-win-btn glass-glare" style="width:26px;height:26px;" title="Настройки OmniWave">
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9c.26.604.852.997 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>
            </svg>
          </button>
          <button id="btnCollapseSidebar" class="top-win-btn glass-glare" title="Скрыть панель">‹</button>
        </div>
      </div>

      <!-- 3-Tab Capsule -->
      <div class="tab-capsule">
        <div id="tabSlider" class="tab-slider"></div>
        <button class="tab-btn active" data-tab="0">Поиск</button>
        <button class="tab-btn" data-tab="1">Любимое</button>
        <button class="tab-btn" data-tab="2">Списки</button>
      </div>

      <!-- TAB 0: SEARCH -->
      <div id="viewSearch" style="flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;">
        <div style="flex-shrink:0;padding:0 14px 8px;">
          <div class="search-input-wrap">
            <button id="btnSearchRun" style="border:none;background:transparent;color:rgba(255,255,255,0.6);cursor:pointer;display:flex;align-items:center;" title="Искать">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
            </button>
            <input id="searchInput" type="text" placeholder="Поиск треков или ссылка..."
                   style="flex:1;min-width:0;background:transparent;border:none;outline:none;color:#fff;font-family:inherit;font-size:12.5px;"/>
            <button id="btnClearSearch" style="display:none;width:18px;height:18px;border-radius:50%;border:none;background:rgba(255,255,255,0.2);color:#fff;font-size:10px;cursor:pointer;align-items:center;justify-content:center;">✕</button>
          </div>
        </div>

        <div id="filterPillsWrap" class="filter-pills-wrap"></div>

        <div id="searchStatusText" style="flex-shrink:0;padding:0 14px 6px;font-size:10.5px;color:rgba(255,255,255,0.58);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
          Введите запрос или выберите «Радио»
        </div>

        <div id="resultsList" class="glass-scroll" style="flex:1;min-height:0;overflow-y:auto;padding:0 10px 6px 14px;display:flex;flex-direction:column;gap:5px;"></div>

        <div style="flex-shrink:0;padding:8px 14px 12px;border-top:1px solid rgba(255,255,255,0.12);display:flex;align-items:center;gap:6px;">
          <button id="btnScrollUp" class="card-icon-btn glass-glare" style="width:30px;height:30px;border-radius:10px;" title="Вверх">▲</button>
          <button id="btnScrollDown" class="card-icon-btn glass-glare" style="width:30px;height:30px;border-radius:10px;" title="Вниз">▼</button>
          <button id="btnLoadMore" class="top-pill-btn glass-glare" style="flex:1;height:30px;border-radius:10px;justify-content:center;" disabled>
            Загрузить ещё
          </button>
        </div>
      </div>

      <!-- TAB 1: FAVORITES -->
      <div id="viewFavorites" style="display:none;flex:1;flex-direction:column;min-height:0;overflow:hidden;">
        <div style="flex-shrink:0;padding:0 14px 8px;display:flex;align-items:center;justify-content:space-between;gap:6px;flex-wrap:wrap;">
          <span id="favoritesCountText" style="font-size:12px;font-weight:600;color:rgba(255,255,255,0.68);">0 треков</span>
          <div style="display:flex;align-items:center;gap:5px;">
            <button id="btnExportFavYandex" style="padding:5px 10px;border-radius:9px;border:1px solid rgba(255,204,0,0.48);background:rgba(255,204,0,0.18);color:#FFCC00;font-family:inherit;font-size:11px;font-weight:800;cursor:pointer;display:inline-flex;align-items:center;gap:4px;transition:transform 0.18s var(--spring),background 0.16s ease;" title="Перенести все треки из «Любимого» в Яндекс Музыку («Мне нравится»)">
              <span>✦</span><span id="btnExportFavYandexText">В Яндекс</span>
            </button>
            <button id="btnPlayAllFav" style="padding:5px 11px;border-radius:9px;border:none;background:#fff;color:#000;font-family:inherit;font-size:11px;font-weight:800;cursor:pointer;box-shadow:0 4px 14px rgba(255,255,255,0.22);">▶ Играть всё</button>
            <button id="btnShuffleFav" class="card-icon-btn glass-glare" style="width:28px;height:28px;" title="Перемешать избранное">
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 3 21 3 21 8"/><line x1="4" y1="20" x2="21" y2="3"/><polyline points="21 16 21 21 16 21"/><line x1="15" y1="15" x2="21" y2="21"/><line x1="4" y1="4" x2="9" y2="9"/></svg>
            </button>
          </div>
        </div>
        <div id="favTransferBanner" style="display:none;flex-shrink:0;margin:0 14px 8px;padding:8px 10px;border-radius:11px;background:rgba(255,204,0,0.12);border:1px solid rgba(255,204,0,0.35);font-size:11px;color:#fff;">
          <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;">
            <span id="favTransferText" style="flex:1;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-weight:600;color:#FFCC00;">Перенос в Яндекс Музыку...</span>
            <button id="btnCloseFavBanner" style="border:none;background:transparent;color:rgba(255,255,255,0.6);font-size:11px;cursor:pointer;padding:0 2px;" title="Скрыть">✕</button>
          </div>
          <div id="favTransferBarWrap" style="margin-top:6px;height:4px;border-radius:999px;background:rgba(255,255,255,0.14);overflow:hidden;">
            <div id="favTransferBarFill" style="height:100%;width:0%;background:#FFCC00;border-radius:999px;transition:width 0.2s ease;"></div>
          </div>
        </div>
        <div style="flex-shrink:0;padding:0 14px 8px;">
          <div class="search-input-wrap" style="padding:5px 10px;">
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="rgba(255,255,255,0.55)" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
            <input id="favoritesSearchInput" type="text" placeholder="Поиск в любимых треках..."
                   style="flex:1;min-width:0;background:transparent;border:none;outline:none;color:#fff;font-family:inherit;font-size:12px;"/>
            <button id="btnClearFavSearch" style="display:none;width:18px;height:18px;border-radius:50%;border:none;background:rgba(255,255,255,0.2);color:#fff;font-size:10px;cursor:pointer;align-items:center;justify-content:center;" title="Очистить">✕</button>
          </div>
        </div>
        <div id="favoritesList" class="glass-scroll" style="flex:1;min-height:0;overflow-y:auto;padding:0 10px 12px 14px;display:flex;flex-direction:column;gap:5px;"></div>
      </div>

      <!-- TAB 2: PLAYLISTS -->
      <div id="viewPlaylists" style="display:none;flex:1;flex-direction:column;min-height:0;overflow:hidden;">
        <div id="playlistsOverview" style="flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;">
          <div style="flex-shrink:0;padding:0 14px 8px;display:flex;align-items:center;justify-content:space-between;gap:6px;">
            <div style="display:flex;align-items:center;gap:6px;">
              <span style="font-size:11.5px;font-weight:600;color:rgba(255,255,255,0.68);">Синхронизировано</span>
              <button id="btnSyncPlaylists" class="card-icon-btn glass-glare" style="width:24px;height:24px;" title="Обновить плейлисты из аккаунтов">↻</button>
            </div>
            <button id="btnToggleNewPl" style="padding:4px 9px;border-radius:8px;border:1px solid rgba(255,204,0,0.45);background:rgba(255,204,0,0.18);color:#FFCC00;font-family:inherit;font-size:11px;font-weight:700;cursor:pointer;">
              + Новый список
            </button>
          </div>

          <div id="newPlForm" style="display:none;flex-shrink:0;padding:0 14px 8px;">
            <div class="search-input-wrap" style="padding:5px 8px;">
              <input id="newPlInput" type="text" placeholder="Название плейлиста..."
                     style="flex:1;min-width:0;background:transparent;border:none;outline:none;color:#fff;font-family:inherit;font-size:12px;"/>
              <button id="btnCreatePl" style="padding:4px 9px;border-radius:7px;border:none;background:#FFCC00;color:#000;font-family:inherit;font-size:11px;font-weight:800;cursor:pointer;">ОК</button>
            </div>
          </div>

          <div style="flex-shrink:0;padding:0 14px 8px;">
            <div class="search-input-wrap" style="padding:5px 10px;">
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="rgba(255,255,255,0.55)" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
              <input id="playlistsOverviewSearchInput" type="text" placeholder="Поиск плейлиста или трека в списках..."
                     style="flex:1;min-width:0;background:transparent;border:none;outline:none;color:#fff;font-family:inherit;font-size:12px;"/>
              <button id="btnClearPlOverviewSearch" style="display:none;width:18px;height:18px;border-radius:50%;border:none;background:rgba(255,255,255,0.2);color:#fff;font-size:10px;cursor:pointer;align-items:center;justify-content:center;" title="Очистить">✕</button>
            </div>
          </div>

          <div id="playlistsList" class="glass-scroll" style="flex:1;min-height:0;overflow-y:auto;padding:0 10px 12px 14px;display:flex;flex-direction:column;gap:6px;"></div>
        </div>

        <div id="playlistDetail" style="display:none;flex:1;flex-direction:column;min-height:0;overflow:hidden;">
          <div style="flex-shrink:0;padding:0 14px 8px;display:flex;align-items:center;justify-content:space-between;gap:8px;">
            <button id="btnBackPlaylists" class="card-icon-btn glass-glare" style="width:26px;height:26px;" title="Назад">←</button>
            <div style="flex:1;min-width:0;">
              <div id="detailPlTitle" style="font-size:13px;font-weight:800;color:#fff;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">Плейлист</div>
              <div id="detailPlCount" style="font-size:10.5px;color:rgba(255,255,255,0.55);">0 треков</div>
            </div>
            <button id="btnPlayOpenedPl" style="padding:5px 10px;border-radius:8px;border:none;background:#FFCC00;color:#000;font-family:inherit;font-size:11px;font-weight:800;cursor:pointer;">▶ Играть</button>
          </div>
          <div style="flex-shrink:0;padding:0 14px 8px;">
            <div class="search-input-wrap" style="padding:5px 10px;">
              <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="rgba(255,255,255,0.55)" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
              <input id="playlistTrackSearchInput" type="text" placeholder="Поиск трека в этом плейлисте..."
                     style="flex:1;min-width:0;background:transparent;border:none;outline:none;color:#fff;font-family:inherit;font-size:12px;"/>
              <button id="btnClearPlTrackSearch" style="display:none;width:18px;height:18px;border-radius:50%;border:none;background:rgba(255,255,255,0.2);color:#fff;font-size:10px;cursor:pointer;align-items:center;justify-content:center;" title="Очистить">✕</button>
            </div>
          </div>
          <div id="playlistTracksList" class="glass-scroll" style="flex:1;min-height:0;overflow-y:auto;padding:0 10px 12px 14px;display:flex;flex-direction:column;gap:5px;"></div>
        </div>
      </div>
    </div>
  </aside>

  <!-- CENTRAL CLEAR GLASS STAGE -->
  <section id="centerStage">
    <div class="stage-orb stage-orb-1"></div>
    <div class="stage-orb stage-orb-2"></div>
    <div class="stage-orb stage-orb-3"></div>

    <!-- Top Drag & Control Bar -->
    <header id="stageTopBar" class="stage-top-bar">
      <button id="btnToggleSidebar" class="top-pill-btn glass-glare">☰ Скрыть панель</button>

      <div style="display:flex;align-items:center;gap:6px;flex:1 1 auto;flex-wrap:wrap;justify-content:flex-end;min-width:0;">
        <button id="btnYandexWave" class="top-pill-btn glass-glare" title="Запустить «Мою волну» Яндекса" style="border-color:rgba(255,204,0,0.45);color:#FFD84D;">🌊 Моя волна</button>
        <button id="btnVkMix" class="top-pill-btn glass-glare" title="Запустить бесконечный поток «VK Микс»" style="border-color:rgba(0,119,255,0.5);color:#6EB4FF;">🔥 VK Микс</button>
        <button class="top-pill-btn glass-glare" data-drawer="0">Текст</button>
        <button class="top-pill-btn glass-glare" data-drawer="1">Очередь</button>
        <button class="top-pill-btn glass-glare" data-drawer="2">Звук</button>
        <button id="btnHideToIsland" class="top-pill-btn glass-glare">Свернуть в Остров</button>
        <button id="btnWinMin" class="top-win-btn glass-glare" title="Свернуть">—</button>
        <button id="btnWinMax" class="top-win-btn glass-glare" title="На весь экран">□</button>
        <button id="btnWinClose" class="top-win-btn close-btn glass-glare" title="Скрыть в Остров">✕</button>
      </div>
    </header>

    <!-- Center Hero -->
    <div class="center-hero">
      <div id="heroSourceBadge" class="glass-surface" style="padding:4px 13px;border-radius:999px;font-size:11.5px;font-weight:800;color:#FFCC00;background:rgba(255,204,0,0.16);border-color:rgba(255,204,0,0.4);flex-shrink:0;">
        OmniWave
      </div>

      <h1 id="heroArtist" style="font-size:clamp(26px,3.2vw,42px);font-weight:800;max-width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;text-align:center;line-height:1.12;flex-shrink:0;text-shadow:0 4px 20px rgba(0,0,0,0.4);">
        OmniWave
      </h1>

      <div id="heroCoverBox">
        <img id="heroCoverImg" alt="" style="width:100%;height:100%;object-fit:cover;display:none;"/>
        <div id="heroCoverPlaceholder" style="width:100%;height:100%;display:flex;align-items:center;justify-content:center;background:rgba(255,255,255,0.08);color:rgba(255,255,255,0.45);font-size:42px;">
          ♫
        </div>
        <div id="heroSeekOverlay" class="hero-seek-overlay">
          <span id="heroSeekOverlayDelta" class="hero-seek-overlay-delta">+15 с</span>
          <span id="heroSeekOverlayTime" class="hero-seek-overlay-time">0:00</span>
        </div>
      </div>

      <!-- Controls & Glass Progress Capsule -->
      <div class="hero-capsule-row">
        <button id="btnMute" class="hero-circle-btn glass-glare" title="Звук / Без звука"></button>
        <button id="btnShuffle" class="hero-circle-btn glass-glare" title="Перемешать очередь">
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><polyline points="16 3 21 3 21 8"/><line x1="4" y1="20" x2="21" y2="3"/><polyline points="21 16 21 21 16 21"/><line x1="15" y1="15" x2="21" y2="21"/><line x1="4" y1="4" x2="9" y2="9"/></svg>
        </button>

        <div class="hero-capsule-wrap">
          <div id="heroSeekTooltip" class="hero-seek-tooltip">0:00</div>
          <div id="heroProgressCapsule" class="glass-progress-capsule glass-glare" title="Наведите или потяните для точной перемотки">
            <div id="heroHoverFill" class="glass-hover-fill"></div>
            <div id="heroProgressFill" class="glass-progress-fill"></div>
            <span id="heroCurTime" style="position:relative;z-index:2;font-size:12px;font-weight:700;color:rgba(255,255,255,0.92);font-variant-numeric:tabular-nums;flex-shrink:0;">0:00</span>
            <span id="heroTrackTitle" style="position:relative;z-index:2;font-size:14px;font-weight:800;color:#fff;letter-spacing:0.02em;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding:0 14px;text-shadow:0 2px 10px rgba(0,0,0,0.45);">
              Выберите трек слева
            </span>
            <span id="heroTotTime" style="position:relative;z-index:2;font-size:12px;font-weight:600;color:rgba(255,255,255,0.78);font-variant-numeric:tabular-nums;flex-shrink:0;">0:00</span>
            <div class="glass-progress-rail">
              <div id="heroProgressRailFill" class="glass-progress-rail-fill"></div>
            </div>
          </div>
        </div>

        <button id="btnHeroLike" class="hero-circle-btn glass-glare" title="В избранное"></button>
        <button id="btnHeroDl" class="hero-circle-btn glass-glare" title="Скачать трек"></button>
        <button id="btnHeroMore" class="hero-circle-btn glass-glare" title="Действия с треком">⋯</button>
      </div>

      <!-- Playback Buttons Row -->
      <div style="display:flex;align-items:center;gap:12px;flex-shrink:0;">
        <button id="btnRepeat" class="hero-circle-btn glass-glare" style="width:38px;height:38px;" title="Повтор: Выкл">
          <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><polyline points="17 1 21 5 17 9"/><path d="M3 11V9a4 4 0 0 1 4-4h14"/><polyline points="7 23 3 19 7 15"/><path d="M21 13v2a4 4 0 0 1-4 4H3"/></svg>
          <span id="repeat1Badge" style="display:none;position:absolute;top:-3px;right:-3px;width:14px;height:14px;border-radius:50%;background:#FFCC00;color:#000;font-size:9px;font-weight:900;align-items:center;justify-content:center;">1</span>
        </button>

        <button id="btnSeekBack15" class="hero-circle-btn glass-glare" style="width:38px;height:38px;font-size:11px;font-weight:800;" title="Назад на 15 сек">-15</button>

        <button id="btnPrev" class="hero-circle-btn glass-glare" style="width:44px;height:44px;" title="Предыдущий трек">
          <svg width="17" height="17" viewBox="0 0 24 24" fill="currentColor"><path d="M6 6h2v12H6zm3.5 6 8.5 6V6z"/></svg>
        </button>

        <button id="btnPlayPause" class="hero-play-btn glass-glare" title="Пауза / Воспроизведение"></button>

        <button id="btnNext" class="hero-circle-btn glass-glare" style="width:44px;height:44px;" title="Следующий трек">
          <svg width="17" height="17" viewBox="0 0 24 24" fill="currentColor"><path d="M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z"/></svg>
        </button>

        <button id="btnSeekFwd15" class="hero-circle-btn glass-glare" style="width:38px;height:38px;font-size:11px;font-weight:800;" title="Вперёд на 15 сек">+15</button>

        <div style="width:38px;height:38px;pointer-events:none;"></div>
      </div>

      <!-- Volume Slider -->
      <div style="display:flex;align-items:center;gap:8px;flex-shrink:0;">
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="rgba(255,255,255,0.65)" stroke-width="2" stroke-linecap="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/></svg>
        <input id="volumeSlider" type="range" min="0" max="100" value="80" class="h-slider" style="width:145px;"/>
      </div>

      <!-- 3-Line Interactive Karaoke -->
      <div id="stageKaraoke" style="text-align:center;width:min(540px,100%);flex-shrink:0;display:flex;flex-direction:column;gap:3px;overflow:hidden;">
        <div id="lyricPrev" class="lyric-line" style="font-size:12px;color:rgba(255,255,255,0.44);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:pointer;"></div>
        <div id="lyricCurr" class="lyric-line" style="font-size:15.5px;font-weight:800;color:#fff;text-shadow:0 0 22px rgba(var(--accent-rgb),0.55);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:pointer;"></div>
        <div id="lyricNext" class="lyric-line" style="font-size:12px;color:rgba(255,255,255,0.44);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:pointer;"></div>
      </div>
    </div>

    <!-- Bottom Status Line -->
    <div id="stageStatusText" style="flex-shrink:0;text-align:center;font-size:11.5px;color:rgba(255,255,255,0.68);padding:4px 16px 12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;z-index:4;">
      OmniWave • Clear Liquid Glass
    </div>
  </section>

  <!-- RIGHT SLIDE-OVER CLEAR GLASS DRAWER -->
  <aside id="rightDrawer">
    <div class="drawer-inner">
      <div style="flex-shrink:0;padding:12px 14px;border-bottom:1px solid rgba(255,255,255,0.14);display:flex;align-items:center;justify-content:space-between;gap:6px;">
        <span id="drawerHeaderTitle" style="font-size:13px;font-weight:800;color:rgba(255,255,255,0.92);letter-spacing:0.02em;">Очередь</span>
        <button id="btnCloseDrawer" class="top-win-btn glass-glare" title="Закрыть панель">✕</button>
      </div>

      <!-- Drawer 0: Lyrics -->
      <div id="drawerLyricsView" style="flex:1;display:flex;flex-direction:column;min-height:0;overflow:hidden;padding:12px;">
        <div style="flex-shrink:0;margin-bottom:10px;padding:0 4px;">
          <div id="lyricsHeaderTitle" style="font-size:14.5px;font-weight:700;color:#fff;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">Текст песни</div>
          <div id="lyricsSourceInfo" style="font-size:11px;color:rgba(255,255,255,0.52);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;"></div>
        </div>
        <div id="lyricsBodyScroll" class="glass-scroll" style="flex:1;min-height:0;overflow-y:auto;display:flex;flex-direction:column;gap:4px;"></div>
      </div>

      <!-- Drawer 1: Queue -->
      <div id="drawerQueueView" style="display:none;flex:1;flex-direction:column;min-height:0;overflow:hidden;">
        <div style="flex-shrink:0;padding:9px 14px;border-bottom:1px solid rgba(255,255,255,0.12);display:flex;align-items:center;justify-content:space-between;">
          <span id="queueSummaryText" style="font-size:11px;color:rgba(255,255,255,0.6);">0 треков</span>
          <div style="display:flex;gap:10px;">
            <button id="btnQueueShuffle" style="border:none;background:transparent;color:#FFCC00;font-family:inherit;font-size:11px;font-weight:700;cursor:pointer;">Перемешать</button>
            <button id="btnQueueClear" style="border:none;background:transparent;color:rgba(255,255,255,0.6);font-family:inherit;font-size:11px;cursor:pointer;">Очистить</button>
          </div>
        </div>
        <div id="queueList" class="glass-scroll" style="flex:1;min-height:0;overflow-y:auto;padding:8px 10px 12px;display:flex;flex-direction:column;gap:5px;"></div>
      </div>

      <!-- Drawer 2: Sound & 10-Band EQ -->
      <div id="drawerSoundView" class="glass-scroll" style="display:none;flex:1;min-height:0;overflow-y:auto;padding:14px;flex-direction:column;gap:14px;">
        <div style="display:flex;align-items:center;justify-content:space-between;">
          <div>
            <div style="font-size:13px;font-weight:700;">10-полосный эквалайзер</div>
            <div style="font-size:10.5px;color:rgba(255,255,255,0.52);">Встроенный плеер</div>
          </div>
          <button id="btnEqToggle" class="top-pill-btn" style="padding:4px 11px;font-size:11px;">Выкл</button>
        </div>

        <div>
          <div style="font-size:10px;font-weight:700;color:rgba(255,255,255,0.5);margin-bottom:6px;letter-spacing:0.05em;">ПРЕСЕТЫ</div>
          <div id="eqPresetsWrap" style="display:flex;flex-wrap:wrap;gap:5px;"></div>
        </div>

        <div>
          <div style="display:flex;justify-content:space-between;font-size:11.5px;margin-bottom:5px;">
            <span style="color:rgba(255,255,255,0.8);">Предусиление (Preamp)</span>
            <span id="preampValText" style="color:#FFCC00;font-weight:700;">0.0 дБ</span>
          </div>
          <input id="preampSlider" type="range" min="-12" max="12" step="0.5" value="0" class="h-slider" style="width:100%;"/>
        </div>

        <div>
          <div style="font-size:10px;font-weight:700;color:rgba(255,255,255,0.5);margin-bottom:6px;letter-spacing:0.05em;">ЧАСТОТЫ (±12 ДБ)</div>
          <div class="glass-surface" style="padding:10px 6px;border-radius:14px;background:rgba(255,255,255,0.05);">
            <div id="eqBandsGrid" style="display:grid;grid-template-columns:repeat(10,1fr);gap:2px;align-items:end;"></div>
          </div>
        </div>

        <div>
          <div style="font-size:10px;font-weight:700;color:rgba(255,255,255,0.5);margin-bottom:6px;letter-spacing:0.05em;">СКОРОСТЬ ВОСПРОИЗВЕДЕНИЯ</div>
          <div id="rateButtonsWrap" style="display:flex;flex-wrap:wrap;gap:5px;"></div>
        </div>
      </div>
    </div>
  </aside>
</div>

<!-- FLOATING CLEAR GLASS CONTEXT MENUS -->
<div id="playlistMenu" class="floating-menu">
  <div style="padding:4px 10px 6px;font-size:10.5px;font-weight:700;text-transform:uppercase;letter-spacing:0.05em;color:rgba(255,255,255,0.48);">
    Добавить в плейлист
  </div>
  <div id="playlistMenuItems"></div>
  <div class="f-menu-sep"></div>
  <button id="menuExportYandex" class="f-menu-item"><span style="color:#FFCC00;font-weight:700;">✦ В Яндекс «Мне нравится»</span></button>
  <button id="menuQuickCreatePl" class="f-menu-item"><span>＋ Новый микс из трека</span></button>
  <button id="menuPlayNext" class="f-menu-item"><span>⏭ Включить следующим</span></button>
  <button id="menuAddQueue" class="f-menu-item"><span>☰ В конец очереди</span></button>
</div>

<div id="heroMoreMenu" class="floating-menu">
  <button id="optRepeat" class="f-menu-item"><span>🔁 Повторять</span><span id="optRepeatLabel" style="font-size:11px;font-weight:700;color:#FFCC00;">Выкл</span></button>
  <button id="optLyrics" class="f-menu-item"><span>📝 Показать текст песни</span></button>
  <button id="optQueue" class="f-menu-item"><span>☰ Открыть очередь</span></button>
  <button id="optSound" class="f-menu-item"><span>🎚 Настройки звука</span></button>
  <div class="f-menu-sep"></div>
  <button id="optExportYandex" class="f-menu-item"><span style="color:#FFCC00;font-weight:700;">✦ В Яндекс «Мне нравится»</span></button>
  <button id="optDownload" class="f-menu-item"><span id="optDlText">⬇ Скачать трек</span></button>
  <button id="optPlaylist" class="f-menu-item"><span>＋ Добавить в плейлист</span><span>›</span></button>
</div>

<script>
const postCmd = (type, data = {}) => {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify({ type, ...data }));
  }
};

const FILTERS = [
  { key: 'all', label: 'Все', color: '#FFCC00', fg: '#111111' },
  { key: 'yandex_wave', label: '🌊 Моя волна', color: '#FFCC00', fg: '#111111', auth: 'yandex' },
  { key: 'vk_mix', label: '🔥 VK Микс', color: '#0077FF', fg: '#FFFFFF', auth: 'vk' },
  { key: 'yandex', label: 'Яндекс', color: '#FFCC00', fg: '#111111', auth: 'yandex' },
  { key: 'vk', label: 'ВК', color: '#0077FF', fg: '#FFFFFF', auth: 'vk' },
  { key: 'soundcloud', label: 'SoundCloud', color: '#FF5500', fg: '#FFFFFF' },
  { key: 'youtube', label: 'YouTube', color: '#FF0033', fg: '#FFFFFF' },
  { key: 'audius', label: 'Audius', color: '#CC0FF0', fg: '#FFFFFF' },
  { key: 'bandcamp', label: 'Bandcamp', color: '#1DA0C3', fg: '#FFFFFF' },
  { key: 'open_catalog', label: 'Архив / Hearthis', color: '#22C55E', fg: '#FFFFFF' },
  { key: 'radio', label: 'Радио', color: '#F59E0B', fg: '#111111' }
];

const EQ_LABELS = ['60', '170', '310', '600', '1k', '3k', '6k', '12k', '14k', '16k'];
const RATES = [0.75, 0.9, 1.0, 1.15, 1.25, 1.5];

const st = {
  sidebarCollapsed: false,
  leftTab: 0,
  drawerOpen: false,
  drawerTab: 0,
  filter: 'all',
  yandexConnected: false,
  vkConnected: false,
  results: [],
  favorites: [],
  favoritesQuery: '',
  playlists: [],
  playlistOverviewQuery: '',
  playlistTrackQuery: '',
  openedPlaylistId: null,
  queue: [],
  currentTrack: null,
  isPlaying: false,
  currentMs: 0,
  totalMs: 0,
  volume: 80,
  repeatMode: 0,
  lyrics: null,
  activeLyricIdx: -1,
  eq: { enabled: false, preset: 'Flat', preamp: 0, bands: new Array(10).fill(0), rate: 1.0, presets: {} },
  targetTrackKey: null,
  scrubbing: false
};

const SVG_PLAY = '<svg width="21" height="21" viewBox="0 0 24 24" fill="currentColor"><path d="M8 5v14l11-7z"/></svg>';
const SVG_PAUSE = '<svg width="21" height="21" viewBox="0 0 24 24" fill="currentColor"><path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/></svg>';
const SVG_HEART_OFF = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>';
const SVG_HEART_ON = '<svg width="15" height="15" viewBox="0 0 24 24" fill="#FF4D6D" stroke="#FF4D6D" stroke-width="2"><path d="M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78l1.06 1.06L12 21.23l7.78-7.78 1.06-1.06a5.5 5.5 0 0 0 0-7.78z"/></svg>';
const SVG_VOL_ON = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/></svg>';
const SVG_VOL_OFF = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#FF6E6E" stroke-width="2" stroke-linecap="round"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><line x1="23" y1="9" x2="17" y2="15"/><line x1="17" y1="9" x2="23" y2="15"/></svg>';
const SVG_DL = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';
const SVG_DONE = '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#22C55E" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>';

function fmtMs(ms) {
  if (!ms || ms < 0) return '0:00';
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const rem = s % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, '0')}:${String(rem).padStart(2, '0')}`
    : `${m}:${String(rem).padStart(2, '0')}`;
}

function esc(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

// Pointer specular glare tracking
document.addEventListener('pointermove', e => {
  const el = e.target.closest('.glass-glare');
  if (!el) return;
  const r = el.getBoundingClientRect();
  el.style.setProperty('--mx', `${e.clientX - r.left}px`);
  el.style.setProperty('--my', `${e.clientY - r.top}px`);
});

// ==================== SIDEBAR & TABS ====================
function setSidebarCollapsed(collapsed, notify = true) {
  st.sidebarCollapsed = !!collapsed;
  document.getElementById('leftSidebar').classList.toggle('collapsed', st.sidebarCollapsed);
  document.getElementById('btnToggleSidebar').textContent = st.sidebarCollapsed ? '☰ Показать панель' : '☰ Скрыть панель';
  if (notify) postCmd('sidebar.setCollapsed', { collapsed: st.sidebarCollapsed });
}

function selectLeftTab(idx) {
  st.leftTab = idx;
  document.getElementById('tabSlider').style.transform = `translateX(${idx * 100}%)`;
  document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', Number(b.dataset.tab) === idx));
  document.getElementById('viewSearch').style.display = idx === 0 ? 'flex' : 'none';
  document.getElementById('viewFavorites').style.display = idx === 1 ? 'flex' : 'none';
  document.getElementById('viewPlaylists').style.display = idx === 2 ? 'flex' : 'none';
}

document.querySelectorAll('.tab-btn').forEach(b => b.onclick = () => selectLeftTab(Number(b.dataset.tab)));
document.getElementById('btnCollapseSidebar').onclick = () => setSidebarCollapsed(true);
document.getElementById('btnToggleSidebar').onclick = () => setSidebarCollapsed(!st.sidebarCollapsed);

// ==================== FILTERS & SEARCH ====================
function renderFilters() {
  const wrap = document.getElementById('filterPillsWrap');
  wrap.innerHTML = '';
  if ((st.filter === 'yandex' && !st.yandexConnected) || (st.filter === 'vk' && !st.vkConnected)) {
    st.filter = 'all';
  }
  FILTERS.forEach(f => {
    const b = document.createElement('button');
    b.className = 'filter-pill';
    b.textContent = f.label;
    const disabled = (f.auth === 'yandex' && !st.yandexConnected) || (f.auth === 'vk' && !st.vkConnected);
    b.disabled = disabled;
    if (disabled) b.title = `Доступно после входа в ${f.label}`;
    if (st.filter === f.key) {
      b.style.background = f.color;
      b.style.color = f.fg;
      b.style.borderColor = f.color;
      b.style.boxShadow = `0 4px 16px ${f.color}44, inset 0 1px 0 rgba(255,255,255,0.45)`;
    }
    b.onclick = () => {
      if (f.key === 'yandex_wave') {
        postCmd('yandex.startWave');
        return;
      }
      if (f.key === 'vk_mix') {
        postCmd('vk.startMix');
        return;
      }
      st.filter = (st.filter === f.key && f.key !== 'all') ? 'all' : f.key;
      renderFilters();
      const q = document.getElementById('searchInput').value.trim();
      if (q || st.filter === 'radio') postCmd('search.run', { query: q, filter: st.filter });
    };
    wrap.appendChild(b);
  });
}

const searchInput = document.getElementById('searchInput');
const btnClearSearch = document.getElementById('btnClearSearch');
searchInput.oninput = () => {
  btnClearSearch.style.display = searchInput.value ? 'inline-flex' : 'none';
};
searchInput.onkeydown = e => {
  if (e.key === 'Enter') postCmd('search.run', { query: searchInput.value.trim(), filter: st.filter });
};
btnClearSearch.onclick = () => {
  searchInput.value = '';
  btnClearSearch.style.display = 'none';
  searchInput.focus();
};
document.getElementById('btnSearchRun').onclick = () => postCmd('search.run', { query: searchInput.value.trim(), filter: st.filter });
document.getElementById('btnLoadMore').onclick = () => postCmd('search.loadMore', { query: searchInput.value.trim(), filter: st.filter });
document.getElementById('btnScrollUp').onclick = () => document.getElementById('resultsList').scrollBy({ top: -240, behavior: 'smooth' });
document.getElementById('btnScrollDown').onclick = () => document.getElementById('resultsList').scrollBy({ top: 240, behavior: 'smooth' });

// ==================== TRACK CARD RENDERER ====================
function createTrackCard(tr, index, listKind, playlistId = null, playlistLabel = null) {
  const isCur = st.currentTrack && st.currentTrack.uniqueKey === tr.uniqueKey;
  const card = document.createElement('div');
  card.className = `track-card ${isCur ? 'active' : ''}`;
  card.dataset.key = tr.uniqueKey;

  const dlHtml = tr.isDownloading
    ? `<span style="font-size:9px;color:#FFCC00;">${tr.downloadProgress}%</span>`
    : (tr.isDownloaded ? '✓' : '↓');
  const dlColor = tr.isDownloaded ? '#22C55E' : '';

  card.innerHTML = `
    <div style="position:relative;width:40px;height:40px;border-radius:9px;overflow:hidden;flex-shrink:0;background:rgba(255,255,255,0.12);display:flex;align-items:center;justify-content:center;">
      ${tr.coverUrl ? `<img src="${esc(tr.coverUrl)}" loading="lazy" style="width:100%;height:100%;object-fit:cover;display:block;" onerror="this.style.display='none'"/>` : '<span style="opacity:0.45;">♫</span>'}
      ${isCur ? '<div class="cover-mini-eq"><span></span><span></span><span></span><span></span></div>' : ''}
    </div>
    <div style="flex:1;min-width:0;">
      <div style="font-size:12.5px;font-weight:700;color:${isCur ? 'var(--accent)' : '#fff'};white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(tr.title)}</div>
      <div style="display:flex;align-items:center;gap:5px;margin-top:2px;min-width:0;">
        <span style="padding:1px 5px;border-radius:4px;font-size:9.5px;font-weight:800;background:${esc(tr.brandColor)};color:#fff;flex-shrink:0;">${esc(tr.source)}</span>
        <span style="font-size:11px;color:rgba(255,255,255,0.62);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(tr.artist)}</span>
        ${tr.durationText ? `<span style="font-size:10px;color:rgba(255,255,255,0.44);flex-shrink:0;">${esc(tr.durationText)}</span>` : ''}
      </div>
      ${playlistLabel ? `<div style="font-size:10px;color:#FFCC00;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">📁 ${esc(playlistLabel)}</div>` : ''}
    </div>
    <div style="display:flex;align-items:center;gap:3px;flex-shrink:0;">
      <button class="card-icon-btn" data-act="play" title="Играть">▶</button>
      <button class="card-icon-btn" data-act="like" style="color:${tr.isLiked ? '#FF4B72' : ''};" title="Нравится">${tr.isLiked ? '♥' : '♡'}</button>
      <button class="card-icon-btn" data-act="dl" style="color:${dlColor};" title="Скачать">${dlHtml}</button>
      <button class="card-icon-btn" data-act="more" title="Ещё">＋</button>
    </div>
  `;

  card.ondblclick = () => postCmd('search.playTrack', { list: listKind, playlistId, trackKey: tr.uniqueKey, index });
  card.onclick = e => {
    const btn = e.target.closest('[data-act]');
    if (!btn) return;
    e.stopPropagation();
    const act = btn.dataset.act;
    if (act === 'play') postCmd('search.playTrack', { list: listKind, playlistId, trackKey: tr.uniqueKey, index });
    else if (act === 'like') postCmd('track.toggleLike', { trackKey: tr.uniqueKey });
    else if (act === 'dl') postCmd('track.download', { trackKey: tr.uniqueKey });
    else if (act === 'more') openPlaylistMenu(tr.uniqueKey, btn, listKind === 'playlist' ? playlistId : null);
  };
  return card;
}

function renderTrackList(containerId, items, listKind, emptyMsg, playlistId = null) {
  const el = document.getElementById(containerId);
  el.innerHTML = '';
  if (!items || items.length === 0) {
    if (emptyMsg) {
      el.innerHTML = `<div style="margin:auto;padding:24px;text-align:center;font-size:12px;color:rgba(255,255,255,0.48);line-height:1.5;">${esc(emptyMsg)}</div>`;
    }
    return;
  }
  const frag = document.createDocumentFragment();
  items.forEach((tr, i) => frag.appendChild(createTrackCard(tr, i, listKind, playlistId)));
  el.appendChild(frag);
}

// ==================== FAVORITES & PLAYLISTS ====================
document.getElementById('btnPlayAllFav').onclick = () => postCmd('favorites.playAll');
document.getElementById('btnShuffleFav').onclick = () => postCmd('favorites.shuffle');
document.getElementById('btnExportFavYandex').onclick = () => postCmd('favorites.exportYandex');
document.getElementById('btnCloseFavBanner').onclick = () => {
  document.getElementById('favTransferBanner').style.display = 'none';
};
document.getElementById('btnSyncPlaylists').onclick = () => postCmd('playlists.sync');
document.getElementById('btnToggleNewPl').onclick = () => {
  const f = document.getElementById('newPlForm');
  const show = f.style.display === 'none';
  f.style.display = show ? 'block' : 'none';
  if (show) document.getElementById('newPlInput').focus();
};
const submitNewPlaylist = () => {
  const inp = document.getElementById('newPlInput');
  const name = inp.value.trim();
  if (!name) return;
  postCmd('playlists.create', { name });
  inp.value = '';
  document.getElementById('newPlForm').style.display = 'none';
};
document.getElementById('btnCreatePl').onclick = submitNewPlaylist;
document.getElementById('newPlInput').onkeydown = e => { if (e.key === 'Enter') submitNewPlaylist(); };
document.getElementById('btnBackPlaylists').onclick = () => {
  st.openedPlaylistId = null;
  resetPlaylistTrackSearchUi();
  renderPlaylists();
};
document.getElementById('btnPlayOpenedPl').onclick = () => {
  if (!st.openedPlaylistId) return;
  const opened = st.playlists.find(p => p.id === st.openedPlaylistId);
  const q = st.playlistTrackQuery.trim();
  if (opened && q) {
    const filtered = filterTracksByQuery(opened.tracks, q);
    if (filtered.length > 0) {
      postCmd('search.playTrack', { list: 'playlist', playlistId: opened.id, trackKey: filtered[0].uniqueKey, index: 0 });
      return;
    }
  }
  postCmd('playlists.play', { id: st.openedPlaylistId });
};

function filterTracksByQuery(tracks, rawQuery) {
  const words = (rawQuery || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return tracks || [];
  return (tracks || []).filter(tr => {
    const hay = `${tr.title || ''} ${tr.artist || ''} ${tr.source || ''}`.toLowerCase();
    return words.every(w => hay.includes(w));
  });
}

function wireLocalFilterInput(inputId, clearBtnId, setter, renderer) {
  const inp = document.getElementById(inputId);
  const clr = document.getElementById(clearBtnId);
  if (!inp || !clr) return;
  const applyVal = val => {
    clr.style.display = val ? 'inline-flex' : 'none';
    setter(val);
    renderer();
  };
  inp.oninput = () => applyVal(inp.value);
  inp.onkeydown = e => {
    if (e.key === 'Escape' && inp.value) {
      e.stopPropagation();
      inp.value = '';
      applyVal('');
    }
  };
  clr.onclick = () => {
    inp.value = '';
    applyVal('');
    inp.focus();
  };
}

function resetPlaylistTrackSearchUi() {
  st.playlistTrackQuery = '';
  const inp = document.getElementById('playlistTrackSearchInput');
  const clr = document.getElementById('btnClearPlTrackSearch');
  if (inp) inp.value = '';
  if (clr) clr.style.display = 'none';
}

wireLocalFilterInput('favoritesSearchInput', 'btnClearFavSearch', q => { st.favoritesQuery = q; }, renderFavorites);
wireLocalFilterInput('playlistsOverviewSearchInput', 'btnClearPlOverviewSearch', q => { st.playlistOverviewQuery = q; }, renderPlaylists);
wireLocalFilterInput('playlistTrackSearchInput', 'btnClearPlTrackSearch', q => { st.playlistTrackQuery = q; }, renderPlaylists);

function renderFavorites() {
  const filtered = filterTracksByQuery(st.favorites, st.favoritesQuery);
  const q = st.favoritesQuery.trim();
  document.getElementById('favoritesCountText').textContent = q
    ? `Найдено: ${filtered.length} из ${st.favorites.length}`
    : `${st.favorites.length} треков`;
  const emptyMsg = q
    ? 'По запросу в любимых треках ничего не найдено.'
    : 'Нажмите ♥ на любом треке, чтобы сохранить его здесь.';
  renderTrackList('favoritesList', filtered, 'favorites', emptyMsg);
}

let draggedPlaylistId = null;

function renderPlaylists() {
  const ov = document.getElementById('playlistsOverview');
  const det = document.getElementById('playlistDetail');
  const opened = st.openedPlaylistId ? st.playlists.find(p => p.id === st.openedPlaylistId) : null;

  if (!opened) {
    st.openedPlaylistId = null;
    ov.style.display = 'flex';
    det.style.display = 'none';
    const wrap = document.getElementById('playlistsList');
    wrap.innerHTML = '';
    if (st.playlists.length === 0) {
      wrap.innerHTML = '<div style="margin:auto;padding:24px;text-align:center;font-size:12px;color:rgba(255,255,255,0.48);">Пока нет плейлистов. Нажмите «+ Новый список».</div>';
      return;
    }

    const qOverview = st.playlistOverviewQuery.trim();
    const overviewWords = qOverview.toLowerCase().split(/\s+/).filter(Boolean);
    const matchingPlaylists = overviewWords.length === 0
      ? st.playlists
      : st.playlists.filter(pl => {
          const hay = `${pl.name || ''} ${pl.platformBadgeText || ''}`.toLowerCase();
          return overviewWords.every(w => hay.includes(w));
        });

    matchingPlaylists.forEach(pl => {
      const row = document.createElement('div');
      row.className = `playlist-row ${pl.isPinned ? 'pinned' : ''}`;
      row.draggable = true;
      row.title = 'Перетащите для изменения порядка';
      row.innerHTML = `
        <div style="width:42px;height:42px;border-radius:10px;overflow:hidden;flex-shrink:0;background:rgba(255,255,255,0.12);display:flex;align-items:center;justify-content:center;">
          ${pl.coverUrl ? `<img src="${esc(pl.coverUrl)}" style="width:100%;height:100%;object-fit:cover;" onerror="this.style.display='none'"/>` : '<span style="opacity:0.45;">♫</span>'}
        </div>
        <div style="flex:1;min-width:0;">
          <div style="font-size:12.5px;font-weight:700;color:#fff;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(pl.name)}</div>
          <div style="display:flex;align-items:center;gap:6px;margin-top:3px;">
            ${pl.showPlatformBadge ? `<span style="padding:1px 6px;border-radius:4px;font-size:9.5px;font-weight:800;background:${esc(pl.platformBadgeColor)};color:${esc(pl.platformBadgeForeground)};">${esc(pl.platformBadgeText)}</span>` : ''}
            <span style="font-size:11px;color:rgba(255,255,255,0.58);">${esc(pl.countText)}</span>
          </div>
        </div>
        <div style="display:flex;align-items:center;gap:2px;flex-shrink:0;">
          <button class="card-icon-btn" data-pact="pin" style="width:22px;height:22px;font-size:11px;color:${pl.isPinned ? '#FFCC00' : ''};opacity:${pl.isPinned ? '1' : '0.48'};" title="${pl.isPinned ? 'Открепить плейлист' : 'Закрепить плейлист'}">📌</button>
          <button class="card-icon-btn" data-pact="up" style="width:22px;height:22px;font-size:11px;" title="Переместить выше">↑</button>
          <button class="card-icon-btn" data-pact="down" style="width:22px;height:22px;font-size:11px;" title="Переместить ниже">↓</button>
          <button class="card-icon-btn" data-pact="play" style="width:22px;height:22px;font-size:11px;" title="Играть">▶</button>
          ${pl.isExternal ? '' : '<button class="card-icon-btn" data-pact="del" style="width:22px;height:22px;font-size:11px;" title="Удалить">✕</button>'}
        </div>
      `;
      row.ondragstart = e => {
        draggedPlaylistId = pl.id;
        row.style.opacity = '0.5';
        if (e.dataTransfer) {
          e.dataTransfer.effectAllowed = 'move';
          e.dataTransfer.setData('text/plain', pl.id);
        }
      };
      row.ondragover = e => {
        if (!draggedPlaylistId || draggedPlaylistId === pl.id) return;
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
        row.classList.add('drag-over');
      };
      row.ondragleave = () => row.classList.remove('drag-over');
      row.ondrop = e => {
        e.preventDefault();
        row.classList.remove('drag-over');
        const srcId = draggedPlaylistId || (e.dataTransfer ? e.dataTransfer.getData('text/plain') : '');
        draggedPlaylistId = null;
        if (srcId && srcId !== pl.id)
          postCmd('playlists.reorder', { sourceId: srcId, targetId: pl.id });
      };
      row.ondragend = () => {
        draggedPlaylistId = null;
        row.style.opacity = '1';
        wrap.querySelectorAll('.drag-over').forEach(el => el.classList.remove('drag-over'));
      };
      row.onclick = e => {
        const pact = e.target.closest('[data-pact]')?.dataset.pact;
        if (pact === 'pin') { e.stopPropagation(); postCmd('playlists.togglePin', { id: pl.id }); return; }
        if (pact === 'up') { e.stopPropagation(); postCmd('playlists.move', { id: pl.id, delta: -1 }); return; }
        if (pact === 'down') { e.stopPropagation(); postCmd('playlists.move', { id: pl.id, delta: 1 }); return; }
        if (pact === 'play') { e.stopPropagation(); postCmd('playlists.play', { id: pl.id }); return; }
        if (pact === 'del') { e.stopPropagation(); postCmd('playlists.delete', { id: pl.id }); return; }
        st.openedPlaylistId = pl.id;
        resetPlaylistTrackSearchUi();
        renderPlaylists();
      };
      wrap.appendChild(row);
    });

    if (qOverview) {
      const matchedTracks = [];
      for (const pl of st.playlists) {
        if (!pl.tracks || pl.tracks.length === 0) continue;
        for (let idx = 0; idx < pl.tracks.length; idx++) {
          const tr = pl.tracks[idx];
          const hay = `${tr.title || ''} ${tr.artist || ''} ${tr.source || ''}`.toLowerCase();
          if (overviewWords.every(w => hay.includes(w))) {
            matchedTracks.push({ tr, idx, pl });
            if (matchedTracks.length >= 60) break;
          }
        }
        if (matchedTracks.length >= 60) break;
      }

      if (matchedTracks.length > 0) {
        const hdr = document.createElement('div');
        hdr.style.cssText = 'padding:8px 2px 2px;font-size:10px;font-weight:800;letter-spacing:0.04em;color:rgba(255,204,0,0.88);';
        hdr.textContent = `НАЙДЕННЫЕ ТРЕКИ В ПЛЕЙЛИСТАХ (${matchedTracks.length}${matchedTracks.length === 60 ? '+' : ''})`;
        wrap.appendChild(hdr);
        matchedTracks.forEach(item => {
          wrap.appendChild(createTrackCard(item.tr, item.idx, 'playlist', item.pl.id, item.pl.name));
        });
      }

      if (matchingPlaylists.length === 0 && matchedTracks.length === 0) {
        wrap.innerHTML = '<div style="margin:auto;padding:24px;text-align:center;font-size:12px;color:rgba(255,255,255,0.48);">По запросу плейлисты и треки не найдены.</div>';
      }
    }
  } else {
    ov.style.display = 'none';
    det.style.display = 'flex';
    document.getElementById('detailPlTitle').textContent = opened.name;
    const qTrack = st.playlistTrackQuery.trim();
    const filteredTracks = filterTracksByQuery(opened.tracks, qTrack);
    document.getElementById('detailPlCount').textContent = qTrack
      ? `Найдено: ${filteredTracks.length} из ${opened.tracks.length}`
      : opened.countText;
    const emptyMsg = qTrack
      ? 'По запросу треков в плейлисте не найдено.'
      : 'Плейлист пуст. Добавьте треки через ＋.';
    renderTrackList('playlistTracksList', filteredTracks, 'playlist', emptyMsg, opened.id);
  }
}

// ==================== RIGHT DRAWER ====================
const DRAWER_TITLES = ['Текст песни', 'Очередь', 'Звук и эквалайзер'];

function syncDrawerViewUi() {
  document.getElementById('drawerLyricsView').style.display = st.drawerTab === 0 ? 'flex' : 'none';
  document.getElementById('drawerQueueView').style.display = st.drawerTab === 1 ? 'flex' : 'none';
  document.getElementById('drawerSoundView').style.display = st.drawerTab === 2 ? 'flex' : 'none';
  const hdr = document.getElementById('drawerHeaderTitle');
  if (hdr) hdr.textContent = DRAWER_TITLES[st.drawerTab] || 'Панель';
  document.querySelectorAll('[data-drawer]').forEach(b => {
    const t = Number(b.dataset.drawer);
    b.classList.toggle('active', st.drawerOpen && t === st.drawerTab);
  });
}

function openDrawer(tab) {
  if (st.drawerOpen && st.drawerTab === tab) {
    st.drawerOpen = false;
  } else {
    st.drawerOpen = true;
    st.drawerTab = tab;
  }
  document.getElementById('rightDrawer').classList.toggle('open', st.drawerOpen);
  syncDrawerViewUi();
}

function showDrawerTab(tab) {
  st.drawerOpen = true;
  st.drawerTab = tab;
  document.getElementById('rightDrawer').classList.add('open');
  syncDrawerViewUi();
}

document.querySelectorAll('[data-drawer]').forEach(b => {
  b.onclick = () => openDrawer(Number(b.dataset.drawer));
});
document.getElementById('btnCloseDrawer').onclick = () => {
  st.drawerOpen = false;
  document.getElementById('rightDrawer').classList.remove('open');
  document.querySelectorAll('[data-drawer]').forEach(b => b.classList.remove('active'));
};

function renderQueue() {
  document.getElementById('queueSummaryText').textContent = `${st.queue.length} треков в очереди`;
  const wrap = document.getElementById('queueList');
  wrap.innerHTML = '';
  if (st.queue.length === 0) {
    wrap.innerHTML = '<div style="margin:auto;padding:24px;text-align:center;font-size:12px;color:rgba(255,255,255,0.48);">Очередь пуста.</div>';
    return;
  }
  st.queue.forEach((tr, i) => {
    const isCur = st.currentTrack && st.currentTrack.uniqueKey === tr.uniqueKey;
    const row = document.createElement('div');
    row.className = `track-card ${isCur ? 'active' : ''}`;
    row.style.padding = '6px 8px';
    row.innerHTML = `
      <div style="width:34px;height:34px;border-radius:8px;overflow:hidden;flex-shrink:0;background:rgba(255,255,255,0.12);display:flex;align-items:center;justify-content:center;">
        ${tr.coverUrl ? `<img src="${esc(tr.coverUrl)}" style="width:100%;height:100%;object-fit:cover;" onerror="this.style.display='none'"/>` : '<span style="opacity:0.45;">♫</span>'}
      </div>
      <div style="flex:1;min-width:0;">
        <div style="font-size:12px;font-weight:700;color:${isCur ? 'var(--accent)' : '#fff'};white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(tr.title)}</div>
        <div style="font-size:10.5px;color:rgba(255,255,255,0.58);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${esc(tr.artist)}</div>
      </div>
      <div style="display:flex;gap:2px;flex-shrink:0;">
        <button class="card-icon-btn" data-q="up" style="width:21px;height:21px;" title="Вверх">↑</button>
        <button class="card-icon-btn" data-q="dn" style="width:21px;height:21px;" title="Вниз">↓</button>
        <button class="card-icon-btn" data-q="rm" style="width:21px;height:21px;" title="Удалить">✕</button>
      </div>
    `;
    row.ondblclick = () => postCmd('queue.playIndex', { index: i });
    row.onclick = e => {
      const q = e.target.closest('[data-q]')?.dataset.q;
      if (!q) return;
      e.stopPropagation();
      if (q === 'up' && i > 0) postCmd('queue.move', { from: i, to: i - 1 });
      else if (q === 'dn' && i < st.queue.length - 1) postCmd('queue.move', { from: i, to: i + 1 });
      else if (q === 'rm') postCmd('queue.remove', { index: i });
    };
    wrap.appendChild(row);
  });
}

document.getElementById('btnQueueShuffle').onclick = () => postCmd('player.shuffle');
document.getElementById('btnQueueClear').onclick = () => postCmd('queue.clear');

// ==================== LYRICS ====================
function renderLyrics() {
  const lyr = st.lyrics;
  const titleEl = document.getElementById('lyricsHeaderTitle');
  const srcEl = document.getElementById('lyricsSourceInfo');
  const body = document.getElementById('lyricsBodyScroll');
  body.innerHTML = '';

  if (!lyr) {
    titleEl.textContent = 'Текст песни';
    srcEl.textContent = '';
    body.innerHTML = '<div style="margin:auto;padding:24px;text-align:center;font-size:12px;color:rgba(255,255,255,0.5);">Включите трек, чтобы загрузить текст.</div>';
    updateStageKaraoke();
    return;
  }

  titleEl.textContent = lyr.title || 'Текст песни';
  srcEl.textContent = lyr.sourceInfo || '';

  if (lyr.hasSynced && lyr.syncedLines && lyr.syncedLines.length > 0) {
    lyr.syncedLines.forEach((line, i) => {
      const isCur = i === st.activeLyricIdx;
      const div = document.createElement('div');
      div.dataset.lidx = i;
      div.style.cssText = `padding:8px 10px;border-radius:10px;font-size:${isCur ? '13.5px' : '12.5px'};font-weight:${isCur ? '800' : '600'};cursor:pointer;line-height:1.35;transition:all 0.18s ease;background:${isCur ? 'rgba(255,255,255,0.14)' : 'transparent'};color:${isCur ? 'var(--accent)' : 'rgba(255,255,255,0.58)'};${isCur ? 'box-shadow:inset 0 1px 0 rgba(255,255,255,0.28);' : ''}`;
      div.textContent = line.text;
      div.onclick = () => postCmd('player.seekMs', { ms: line.timestampMs });
      body.appendChild(div);
    });
  } else if (lyr.plainText) {
    const div = document.createElement('div');
    div.style.cssText = 'padding:4px 6px;font-size:13px;line-height:1.65;color:rgba(255,255,255,0.88);white-space:pre-wrap;';
    div.textContent = lyr.plainText;
    body.appendChild(div);
  } else {
    body.innerHTML = `<div style="margin:auto;padding:24px;text-align:center;font-size:12.5px;color:rgba(255,255,255,0.52);">${esc(lyr.emptyMessage || 'Текст не найден.')}</div>`;
  }
  updateStageKaraoke();
}

function updateStageKaraoke() {
  const p = document.getElementById('lyricPrev');
  const c = document.getElementById('lyricCurr');
  const n = document.getElementById('lyricNext');
  const lyr = st.lyrics;

  if (!lyr) {
    p.textContent = ''; c.textContent = ''; n.textContent = '';
    return;
  }
  if (!lyr.hasSynced || !lyr.syncedLines || lyr.syncedLines.length === 0) {
    const first = lyr.plainText ? lyr.plainText.split(/\r?\n/).find(x => x.trim()) : '';
    p.textContent = '';
    c.textContent = first || '';
    n.textContent = '';
    p.dataset.ms = ''; c.dataset.ms = ''; n.dataset.ms = '';
    return;
  }

  const lines = lyr.syncedLines;
  const idx = st.activeLyricIdx;
  if (idx < 0) {
    p.textContent = ''; p.dataset.ms = '';
    c.textContent = '♪'; c.dataset.ms = '';
    n.textContent = lines[0]?.text || ''; n.dataset.ms = lines[0] ? String(lines[0].timestampMs) : '';
  } else {
    const prev = idx > 0 ? lines[idx - 1] : null;
    const cur = lines[idx];
    const next = idx + 1 < lines.length ? lines[idx + 1] : null;
    p.textContent = prev ? prev.text : '';
    p.dataset.ms = prev ? String(prev.timestampMs) : '';
    c.textContent = cur ? cur.text : '♪';
    c.dataset.ms = cur ? String(cur.timestampMs) : '';
    n.textContent = next ? next.text : '';
    n.dataset.ms = next ? String(next.timestampMs) : '';
  }
}

['lyricPrev', 'lyricCurr', 'lyricNext'].forEach(id => {
  document.getElementById(id).onclick = e => {
    const ms = e.currentTarget.dataset.ms;
    if (ms !== '' && ms !== undefined) postCmd('player.seekMs', { ms: Number(ms) });
  };
});

function syncActiveLyric(ms) {
  const lyr = st.lyrics;
  if (!lyr || !lyr.hasSynced || !lyr.syncedLines || lyr.syncedLines.length === 0) return;
  let found = -1;
  for (let i = 0; i < lyr.syncedLines.length; i++) {
    if (lyr.syncedLines[i].timestampMs <= ms + 220) found = i;
    else break;
  }
  if (found === st.activeLyricIdx) return;
  st.activeLyricIdx = found;
  updateStageKaraoke();
  if (st.drawerOpen && st.drawerTab === 0) {
    renderLyrics();
    const activeEl = document.querySelector(`#lyricsBodyScroll [data-lidx="${found}"]`);
    if (activeEl) activeEl.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }
}

// ==================== EQ & SOUND ====================
function syncEqEnabledUi() {
  const eqBtn = document.getElementById('btnEqToggle');
  if (eqBtn) {
    eqBtn.textContent = st.eq.enabled ? 'Вкл' : 'Выкл';
    eqBtn.classList.toggle('active', st.eq.enabled);
  }
  const pSlider = document.getElementById('preampSlider');
  if (pSlider) {
    pSlider.style.transition = 'opacity 0.18s ease';
    pSlider.style.opacity = st.eq.enabled ? '1' : '0.45';
  }
  const grid = document.getElementById('eqBandsGrid');
  if (grid && grid.parentElement) {
    grid.parentElement.style.transition = 'opacity 0.18s ease';
    grid.parentElement.style.opacity = st.eq.enabled ? '1' : '0.45';
  }
}

function applyCustomEqFromSlider() {
  st.eq.enabled = true;
  st.eq.preset = 'Custom';
  document.querySelectorAll('#eqPresetsWrap .top-pill-btn').forEach(x => x.classList.remove('active'));
  syncEqEnabledUi();
  postCmd('eq.update', { enabled: true, preset: 'Custom', preamp: st.eq.preamp, bands: st.eq.bands });
}

function renderSoundControls() {
  syncEqEnabledUi();

  const pWrap = document.getElementById('eqPresetsWrap');
  pWrap.innerHTML = '';
  Object.keys(st.eq.presets || {}).forEach(name => {
    const b = document.createElement('button');
    const isPresetActive = st.eq.enabled && st.eq.preset === name;
    b.className = `top-pill-btn ${isPresetActive ? 'active' : ''}`;
    b.style.cssText = 'padding:4px 9px;font-size:11px;';
    b.textContent = name;
    b.onclick = () => {
      if (st.eq.enabled && st.eq.preset === name) {
        st.eq.enabled = false;
        postCmd('eq.update', { enabled: false, preset: st.eq.preset, preamp: st.eq.preamp, bands: st.eq.bands });
        renderSoundControls();
      } else {
        postCmd('eq.applyPreset', { preset: name });
      }
    };
    pWrap.appendChild(b);
  });

  const pSlider = document.getElementById('preampSlider');
  pSlider.value = st.eq.preamp;
  document.getElementById('preampValText').textContent = `${st.eq.preamp > 0 ? '+' : ''}${Number(st.eq.preamp).toFixed(1)} дБ`;

  const grid = document.getElementById('eqBandsGrid');
  grid.innerHTML = '';
  EQ_LABELS.forEach((lbl, i) => {
    const col = document.createElement('div');
    col.style.cssText = 'display:flex;flex-direction:column;align-items:center;gap:6px;';
    const sl = document.createElement('input');
    sl.type = 'range';
    sl.min = '-12';
    sl.max = '12';
    sl.step = '0.5';
    sl.value = String((st.eq.bands && st.eq.bands[i]) ?? 0);
    sl.className = 'eq-slider-v';
    sl.oninput = e => {
      st.eq.bands[i] = Number(e.target.value);
      applyCustomEqFromSlider();
    };
    const t = document.createElement('span');
    t.style.cssText = 'font-size:9px;color:rgba(255,255,255,0.52);';
    t.textContent = lbl;
    col.appendChild(sl);
    col.appendChild(t);
    grid.appendChild(col);
  });

  const rWrap = document.getElementById('rateButtonsWrap');
  rWrap.innerHTML = '';
  RATES.forEach(rate => {
    const b = document.createElement('button');
    const active = Math.abs(rate - st.eq.rate) < 0.02;
    b.className = `top-pill-btn ${active ? 'active' : ''}`;
    b.style.cssText = 'padding:4px 10px;font-size:11px;';
    b.textContent = `${rate}x`;
    b.onclick = () => postCmd('eq.setRate', { rate });
    rWrap.appendChild(b);
  });
}

document.getElementById('btnEqToggle').onclick = () => {
  st.eq.enabled = !st.eq.enabled;
  postCmd('eq.update', { enabled: st.eq.enabled, preset: st.eq.preset, preamp: st.eq.preamp, bands: st.eq.bands });
  renderSoundControls();
};
document.getElementById('preampSlider').oninput = e => {
  st.eq.preamp = Number(e.target.value);
  document.getElementById('preampValText').textContent = `${st.eq.preamp > 0 ? '+' : ''}${st.eq.preamp.toFixed(1)} дБ`;
  applyCustomEqFromSlider();
};

// ==================== STAGE & CONTROLS ====================
function renderStage() {
  const tr = st.currentTrack;
  document.getElementById('btnPlayPause').innerHTML = st.isPlaying ? SVG_PAUSE : SVG_PLAY;
  document.getElementById('heroCoverBox').classList.toggle('playing', st.isPlaying);

  document.getElementById('btnMute').innerHTML = st.volume === 0 ? SVG_VOL_OFF : SVG_VOL_ON;
  document.getElementById('volumeSlider').value = st.volume;

  const repBtn = document.getElementById('btnRepeat');
  const repBadge = document.getElementById('repeat1Badge');
  repBtn.style.color = st.repeatMode > 0 ? '#FFCC00' : '';
  repBadge.style.display = st.repeatMode === 2 ? 'flex' : 'none';
  document.getElementById('optRepeatLabel').textContent = ['Выкл', 'Очередь', '1 трек'][st.repeatMode] || 'Выкл';

  const likeBtn = document.getElementById('btnHeroLike');
  likeBtn.innerHTML = tr && tr.isLiked ? SVG_HEART_ON : SVG_HEART_OFF;

  const dlBtn = document.getElementById('btnHeroDl');
  const optDlText = document.getElementById('optDlText');
  if (!tr) {
    dlBtn.innerHTML = SVG_DL;
    optDlText.textContent = '⬇ Скачать трек';
  } else if (tr.isDownloading) {
    dlBtn.innerHTML = `<span style="font-size:10px;color:#FFCC00;font-weight:800;">${tr.downloadProgress}%</span>`;
    optDlText.textContent = `⬇ Скачивание ${tr.downloadProgress}%`;
  } else if (tr.isDownloaded) {
    dlBtn.innerHTML = SVG_DONE;
    optDlText.textContent = '✓ Показать файл в папке';
  } else {
    dlBtn.innerHTML = SVG_DL;
    optDlText.textContent = '⬇ Скачать трек';
  }
}

let seekVisualTimer = null;
function triggerSeekVisualEffect(targetMs, deltaMs) {
  const clamped = st.totalMs > 0
    ? Math.min(st.totalMs, Math.max(0, targetMs))
    : Math.max(0, targetMs);
  st.currentMs = clamped;
  updateProgressUi();
  syncActiveLyric(clamped);

  const cap = document.getElementById('heroProgressCapsule');
  const tip = document.getElementById('heroSeekTooltip');
  const ov = document.getElementById('heroSeekOverlay');
  const ovDelta = document.getElementById('heroSeekOverlayDelta');
  const ovTime = document.getElementById('heroSeekOverlayTime');

  const sign = deltaMs >= 0 ? '+' : '-';
  const absSec = Math.round(Math.abs(deltaMs) / 1000);
  const deltaLabel = absSec > 0 ? `${sign}${absSec} с` : fmtMs(clamped);

  if (ov && ovDelta && ovTime) {
    ovDelta.textContent = deltaLabel;
    ovTime.textContent = st.totalMs > 0 ? `${fmtMs(clamped)} / ${fmtMs(st.totalMs)}` : fmtMs(clamped);
    ov.classList.add('show');
  }
  if (cap) {
    cap.classList.remove('seek-flash');
    void cap.offsetWidth;
    cap.classList.add('seek-flash');
  }
  if (tip && st.totalMs > 0) {
    const pct = Math.min(1, Math.max(0, clamped / Math.max(1, st.totalMs)));
    const r = cap ? cap.getBoundingClientRect() : { width: 320 };
    const relX = r.width * pct;
    tip.textContent = absSec > 0 ? `${fmtMs(clamped)} (${sign}${fmtMs(Math.abs(deltaMs))})` : fmtMs(clamped);
    tip.style.left = `${Math.max(40, Math.min(r.width - 40, relX))}px`;
    tip.classList.add('show');
  }

  if (seekVisualTimer) clearTimeout(seekVisualTimer);
  seekVisualTimer = setTimeout(() => {
    if (ov) ov.classList.remove('show');
    if (cap) cap.classList.remove('seek-flash');
    if (!st.scrubbing && tip) tip.classList.remove('show');
  }, 900);
}

function triggerRelativeSeek(deltaMs) {
  const target = (st.currentMs || 0) + deltaMs;
  triggerSeekVisualEffect(target, deltaMs);
  postCmd('player.seekRelative', { deltaMs });
}

function updateProgressUi() {
  document.getElementById('heroCurTime').textContent = fmtMs(st.currentMs);
  document.getElementById('heroTotTime').textContent = st.totalMs > 0 ? fmtMs(st.totalMs) : (st.currentTrack?.durationText || '0:00');
  const pct = st.totalMs > 0 ? Math.min(100, Math.max(0, (st.currentMs / st.totalMs) * 100)) : 0;
  document.getElementById('heroProgressFill').style.width = `${pct}%`;
  const railFill = document.getElementById('heroProgressRailFill');
  if (railFill) railFill.style.width = `${pct}%`;
}

// Scrubbing & Live Time Preview on Hero Progress Capsule
const capEl = document.getElementById('heroProgressCapsule');
const seekTipEl = document.getElementById('heroSeekTooltip');
const hoverFillEl = document.getElementById('heroHoverFill');
let scrubStartMs = 0;
const previewAtPointer = (e, applyCurrent) => {
  const r = capEl.getBoundingClientRect();
  const relX = Math.min(r.width, Math.max(0, e.clientX - r.left));
  const frac = relX / Math.max(1, r.width);
  if (hoverFillEl) {
    hoverFillEl.style.width = `${frac * 100}%`;
    hoverFillEl.style.opacity = st.totalMs > 0 ? '1' : '0';
  }
  if (seekTipEl && st.totalMs > 0) {
    const targetMs = Math.round(st.totalMs * frac);
    const diffMs = targetMs - (st.scrubbing ? scrubStartMs : (st.currentMs || 0));
    const sign = diffMs >= 0 ? '+' : '-';
    seekTipEl.textContent = Math.abs(diffMs) >= 1000
      ? `${fmtMs(targetMs)} (${sign}${fmtMs(Math.abs(diffMs))})`
      : fmtMs(targetMs);
    seekTipEl.style.left = `${Math.max(40, Math.min(r.width - 40, relX))}px`;
    seekTipEl.classList.add('show');
  }
  if (applyCurrent && st.totalMs > 0) {
    st.currentMs = Math.round(st.totalMs * frac);
    updateProgressUi();
  }
  return frac;
};
capEl.onpointerenter = e => previewAtPointer(e, false);
capEl.onpointerleave = () => {
  if (!st.scrubbing) {
    if (seekTipEl) seekTipEl.classList.remove('show');
    if (hoverFillEl) hoverFillEl.style.opacity = '0';
  }
};
capEl.onpointerdown = e => {
  st.scrubbing = true;
  scrubStartMs = st.currentMs || 0;
  capEl.setPointerCapture(e.pointerId);
  previewAtPointer(e, true);
};
capEl.onpointermove = e => previewAtPointer(e, st.scrubbing);
capEl.onpointerup = e => {
  if (!st.scrubbing) return;
  st.scrubbing = false;
  const frac = previewAtPointer(e, true);
  if (st.totalMs > 0) {
    const targetMs = Math.round(st.totalMs * frac);
    triggerSeekVisualEffect(targetMs, targetMs - scrubStartMs);
  }
  postCmd('player.seekFraction', { fraction: frac });
  const r = capEl.getBoundingClientRect();
  if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) {
    if (hoverFillEl) hoverFillEl.style.opacity = '0';
  }
};

// Floating Context Menus
const plMenu = document.getElementById('playlistMenu');
const moreMenu = document.getElementById('heroMoreMenu');
function closeMenus() {
  plMenu.classList.remove('open');
  moreMenu.classList.remove('open');
}
function anchorMenu(menu, el) {
  closeMenus();
  const r = el.getBoundingClientRect();
  menu.classList.add('open');
  const mw = menu.offsetWidth || 230;
  const mh = menu.offsetHeight || 220;
  const left = Math.max(10, Math.min(window.innerWidth - mw - 10, r.right - mw));
  const top = (r.bottom + 8 + mh > window.innerHeight - 10) ? Math.max(10, r.top - mh - 8) : r.bottom + 8;
  menu.style.left = `${left}px`;
  menu.style.top = `${top}px`;
}

window.addEventListener('mousedown', e => {
  if (!e.target.closest('.floating-menu') && !e.target.closest('#btnHeroMore') && !e.target.closest('[data-act="more"]')) {
    closeMenus();
  }
});

function openPlaylistMenu(trackKey, anchorEl, insidePlaylistId = null) {
  st.targetTrackKey = trackKey;
  const wrap = document.getElementById('playlistMenuItems');
  wrap.innerHTML = '';
  const locals = st.playlists.filter(p => !p.isExternal);
  locals.forEach(pl => {
    const has = pl.tracks.some(t => t.uniqueKey === trackKey);
    const btn = document.createElement('button');
    btn.className = 'f-menu-item';
    btn.innerHTML = `<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${esc(pl.name)}</span><span style="color:#FFCC00;font-weight:800;">${has ? '✓' : '+'}</span>`;
    btn.onclick = () => {
      postCmd('playlists.addTrack', { playlistId: pl.id, trackKey });
      closeMenus();
    };
    wrap.appendChild(btn);
  });
  if (insidePlaylistId) {
    const plObj = st.playlists.find(p => p.id === insidePlaylistId);
    if (plObj && !plObj.isExternal) {
      const rem = document.createElement('button');
      rem.className = 'f-menu-item';
      rem.style.color = '#FF6E6E';
      rem.innerHTML = '<span>Убрать из этого плейлиста</span>';
      rem.onclick = () => {
        postCmd('playlists.removeTrack', { playlistId: insidePlaylistId, trackKey });
        closeMenus();
      };
      wrap.appendChild(rem);
    }
  }
  anchorMenu(plMenu, anchorEl);
}

document.getElementById('menuExportYandex').onclick = () => {
  if (st.targetTrackKey) postCmd('track.exportYandex', { trackKey: st.targetTrackKey });
  closeMenus();
};
document.getElementById('menuQuickCreatePl').onclick = () => {
  if (st.targetTrackKey) postCmd('playlists.quickCreateAndAdd', { trackKey: st.targetTrackKey });
  closeMenus();
};
document.getElementById('menuPlayNext').onclick = () => {
  if (st.targetTrackKey) postCmd('track.addToQueue', { trackKey: st.targetTrackKey, playNext: true });
  closeMenus();
};
document.getElementById('menuAddQueue').onclick = () => {
  if (st.targetTrackKey) postCmd('track.addToQueue', { trackKey: st.targetTrackKey, playNext: false });
  closeMenus();
};

document.getElementById('btnHeroMore').onclick = e => {
  e.stopPropagation();
  anchorMenu(moreMenu, e.currentTarget);
};
document.getElementById('optRepeat').onclick = () => { postCmd('player.cycleRepeat'); closeMenus(); };
document.getElementById('optLyrics').onclick = () => { openDrawer(0); closeMenus(); };
document.getElementById('optQueue').onclick = () => { openDrawer(1); closeMenus(); };
document.getElementById('optSound').onclick = () => { openDrawer(2); closeMenus(); };
document.getElementById('optExportYandex').onclick = () => {
  if (st.currentTrack) postCmd('track.exportYandex', { trackKey: st.currentTrack.uniqueKey });
  closeMenus();
};
document.getElementById('optDownload').onclick = () => {
  if (st.currentTrack) postCmd('track.download', { trackKey: st.currentTrack.uniqueKey });
  closeMenus();
};
document.getElementById('optPlaylist').onclick = e => {
  e.stopPropagation();
  if (st.currentTrack) openPlaylistMenu(st.currentTrack.uniqueKey, document.getElementById('btnHeroMore'));
};

// Window & transport buttons
document.getElementById('stageTopBar').addEventListener('mousedown', e => {
  if (e.button === 0 && !e.target.closest('button')) postCmd('window.dragStart');
});
document.getElementById('btnWinMin').onclick = () => postCmd('window.minimize');
document.getElementById('btnWinMax').onclick = () => postCmd('window.maximizeToggle');
document.getElementById('btnWinClose').onclick = () => postCmd('window.hideToIsland');
document.getElementById('btnHideToIsland').onclick = () => postCmd('window.hideToIsland');
document.getElementById('btnYandexWave').onclick = () => postCmd('yandex.startWave');
document.getElementById('btnVkMix').onclick = () => postCmd('vk.startMix');
document.getElementById('btnSettings').onclick = () => postCmd('window.openSettings');
document.getElementById('btnAccounts').onclick = () => postCmd('window.openAccounts');

document.getElementById('btnPlayPause').onclick = () => postCmd('player.togglePause');
document.getElementById('btnSeekBack15').onclick = () => triggerRelativeSeek(-15000);
document.getElementById('btnPrev').onclick = () => postCmd('player.prev');
document.getElementById('btnNext').onclick = () => postCmd('player.next');
document.getElementById('btnSeekFwd15').onclick = () => triggerRelativeSeek(15000);
document.getElementById('btnRepeat').onclick = () => postCmd('player.cycleRepeat');
document.getElementById('btnShuffle').onclick = () => postCmd('player.shuffle');
document.getElementById('btnMute').onclick = () => postCmd('player.toggleMute');
document.getElementById('btnHeroLike').onclick = () => {
  if (st.currentTrack) postCmd('track.toggleLike', { trackKey: st.currentTrack.uniqueKey });
};
document.getElementById('btnHeroDl').onclick = () => {
  if (st.currentTrack) postCmd('track.download', { trackKey: st.currentTrack.uniqueKey });
};
document.getElementById('volumeSlider').oninput = e => {
  st.volume = Number(e.target.value);
  document.getElementById('btnMute').innerHTML = st.volume === 0 ? SVG_VOL_OFF : SVG_VOL_ON;
  postCmd('player.setVolume', { volume: st.volume });
};

window.addEventListener('keydown', e => {
  const tag = document.activeElement?.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA') {
    if (e.key === 'Escape') document.activeElement.blur();
    return;
  }

  if (e.key === 'MediaPlayPause' || e.key === 'MediaTrackNext' || e.key === 'MediaTrackPrevious' || e.key === 'MediaStop') {
    return;
  }

  const hasMod = e.ctrlKey || e.altKey || e.shiftKey;

  if (e.code === 'Space' || e.code === 'KeyK') {
    e.preventDefault();
    postCmd('player.togglePause');
  } else if (e.code === 'ArrowRight' && !hasMod) {
    e.preventDefault();
    triggerRelativeSeek(15000);
  } else if (e.code === 'ArrowLeft' && !hasMod) {
    e.preventDefault();
    triggerRelativeSeek(-15000);
  } else if (e.code === 'ArrowUp' && !hasMod) {
    e.preventDefault();
    st.volume = Math.min(100, st.volume + 5);
    renderStage();
    postCmd('player.setVolume', { volume: st.volume });
  } else if (e.code === 'ArrowDown' && !hasMod) {
    e.preventDefault();
    st.volume = Math.max(0, st.volume - 5);
    renderStage();
    postCmd('player.setVolume', { volume: st.volume });
  } else if (e.code === 'KeyM' && !hasMod) {
    e.preventDefault();
    postCmd('player.toggleMute');
  } else if (e.code === 'KeyL' && !hasMod) {
    e.preventDefault();
    if (st.currentTrack) postCmd('track.toggleLike', { trackKey: st.currentTrack.uniqueKey });
  } else if (e.code === 'KeyR' && !hasMod) {
    e.preventDefault();
    postCmd('player.cycleRepeat');
  } else if (e.code === 'KeyS' && !hasMod) {
    e.preventDefault();
    postCmd('player.shuffle');
  }
});

// ==================== INCOMING MESSAGES FROM C# ====================
function applyTrackVisuals(msg) {
  const tr = msg.track;
  st.currentTrack = tr;
  if (msg.accentHex) document.documentElement.style.setProperty('--accent', msg.accentHex);
  if (msg.accentRgb) document.documentElement.style.setProperty('--accent-rgb', msg.accentRgb);
  if (msg.orb2Rgb) document.documentElement.style.setProperty('--orb2-rgb', msg.orb2Rgb);
  if (msg.orb3Rgb) document.documentElement.style.setProperty('--orb3-rgb', msg.orb3Rgb);

  const badge = document.getElementById('heroSourceBadge');
  const artist = document.getElementById('heroArtist');
  const title = document.getElementById('heroTrackTitle');
  const img = document.getElementById('heroCoverImg');
  const ph = document.getElementById('heroCoverPlaceholder');

  if (!tr) {
    badge.textContent = 'OmniWave';
    artist.textContent = 'OmniWave';
    title.textContent = 'Выберите трек слева';
    img.style.display = 'none';
    ph.style.display = 'flex';
  } else {
    badge.textContent = tr.source;
    badge.style.color = tr.brandColor || '#FFCC00';
    badge.style.borderColor = `${tr.brandColor || '#FFCC00'}66`;
    badge.style.background = `${tr.brandColor || '#FFCC00'}26`;
    artist.textContent = tr.artist;
    title.textContent = tr.title;

    const src = msg.coverDataUrl || tr.coverUrl;
    if (src) {
      img.src = src;
      img.style.display = 'block';
      ph.style.display = 'none';
    } else {
      img.style.display = 'none';
      ph.style.display = 'flex';
    }
  }
  renderStage();
  renderTrackList('resultsList', st.results, 'search', '');
  renderFavorites();
  renderPlaylists();
  renderQueue();
}

window.chrome?.webview?.addEventListener('message', event => {
  const m = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
  switch (m.type) {
    case 'initState':
      st.yandexConnected = !!m.yandexConnected;
      st.vkConnected = !!m.vkConnected;
      document.getElementById('dotYandex').classList.toggle('on', st.yandexConnected);
      document.getElementById('dotVk').classList.toggle('on', st.vkConnected);
      setSidebarCollapsed(m.sidebarCollapsed, false);
      st.favorites = m.favorites || [];
      st.playlists = m.playlists || [];
      st.queue = m.queue || [];
      st.isPlaying = !!m.isPlaying;
      st.currentMs = m.currentMs || 0;
      st.totalMs = m.totalMs || 0;
      st.volume = m.volume ?? 80;
      st.repeatMode = m.repeatMode || 0;
      if (m.eq) st.eq = m.eq;
      renderFilters();
      applyTrackVisuals(m);
      updateProgressUi();
      renderSoundControls();
      break;

    case 'searchResults':
      st.results = m.items || [];
      document.getElementById('searchStatusText').textContent = m.status || '';
      const loadBtn = document.getElementById('btnLoadMore');
      loadBtn.disabled = !m.canLoadMore;
      loadBtn.textContent = m.isLoading ? 'Загружаем...' : 'Загрузить ещё';
      renderTrackList('resultsList', st.results, 'search', m.items?.length === 0 ? (m.status || 'Ничего не найдено') : '');
      break;

    case 'libraryUpdate':
      st.favorites = m.favorites || [];
      st.playlists = m.playlists || [];
      if (st.currentTrack) {
        if (typeof m.currentTrackLiked === 'boolean') st.currentTrack.isLiked = m.currentTrackLiked;
        if (typeof m.currentTrackDownloaded === 'boolean') st.currentTrack.isDownloaded = m.currentTrackDownloaded;
      }
      renderFavorites();
      renderPlaylists();
      renderTrackList('resultsList', st.results, 'search', '');
      renderStage();
      break;

    case 'accountsUpdate':
      st.yandexConnected = !!m.yandexConnected;
      st.vkConnected = !!m.vkConnected;
      document.getElementById('dotYandex').classList.toggle('on', st.yandexConnected);
      document.getElementById('dotVk').classList.toggle('on', st.vkConnected);
      renderFilters();
      break;

    case 'trackChanged':
      st.currentMs = 0;
      st.totalMs = m.track?.durationMs || 0;
      st.activeLyricIdx = -1;
      applyTrackVisuals(m);
      updateProgressUi();
      break;

    case 'playbackState':
      st.isPlaying = !!m.isPlaying;
      renderStage();
      break;

    case 'position':
      if (!st.scrubbing) {
        st.currentMs = m.currentMs || 0;
        if (m.totalMs > 0) st.totalMs = m.totalMs;
        updateProgressUi();
        syncActiveLyric(st.currentMs);
      }
      break;

    case 'volume':
      st.volume = m.volume ?? 80;
      renderStage();
      break;

    case 'repeatMode':
      st.repeatMode = m.mode || 0;
      renderStage();
      break;

    case 'queueUpdate':
      st.queue = m.items || [];
      renderQueue();
      break;

    case 'lyricsUpdate':
      st.lyrics = m;
      st.activeLyricIdx = -1;
      renderLyrics();
      if (st.currentMs > 0) syncActiveLyric(st.currentMs);
      break;

    case 'trackProgress': {
      const applyProg = list => (list || []).forEach(t => {
        if (t.uniqueKey === m.uniqueKey) {
          t.isDownloading = !!m.isDownloading;
          t.downloadProgress = m.downloadProgress || 0;
          t.isDownloaded = !!m.isDownloaded;
        }
      });
      applyProg(st.results);
      applyProg(st.favorites);
      st.playlists.forEach(p => applyProg(p.tracks));
      if (st.currentTrack && st.currentTrack.uniqueKey === m.uniqueKey) {
        st.currentTrack.isDownloading = !!m.isDownloading;
        st.currentTrack.downloadProgress = m.downloadProgress || 0;
        st.currentTrack.isDownloaded = !!m.isDownloaded;
      }
      renderStage();
      document.querySelectorAll(`.track-card[data-key="${CSS.escape(m.uniqueKey)}"] [data-act="dl"]`).forEach(btn => {
        btn.innerHTML = m.isDownloading
          ? `<span style="font-size:9px;color:#FFCC00;">${m.downloadProgress}%</span>`
          : (m.isDownloaded ? '✓' : '↓');
        btn.style.color = m.isDownloaded ? '#22C55E' : '';
      });
      break;
    }

    case 'status':
      document.getElementById('stageStatusText').textContent = m.message || '';
      break;

    case 'favoritesTransferProgress': {
      const banner = document.getElementById('favTransferBanner');
      const txt = document.getElementById('favTransferText');
      const barWrap = document.getElementById('favTransferBarWrap');
      const barFill = document.getElementById('favTransferBarFill');
      const btn = document.getElementById('btnExportFavYandex');
      const btnTxt = document.getElementById('btnExportFavYandexText');

      banner.style.display = 'block';
      txt.textContent = m.message || '';
      txt.style.color = m.done ? (m.ok ? '#22C55E' : '#FF6E6E') : '#FFCC00';
      banner.style.borderColor = m.done
        ? (m.ok ? 'rgba(34,197,94,0.45)' : 'rgba(255,110,110,0.45)')
        : 'rgba(255,204,0,0.35)';
      banner.style.background = m.done
        ? (m.ok ? 'rgba(34,197,94,0.12)' : 'rgba(255,110,110,0.12)')
        : 'rgba(255,204,0,0.12)';

      if (m.active) {
        btn.disabled = true;
        btn.style.opacity = '0.65';
        btnTxt.textContent = m.total > 0 ? `${m.current}/${m.total}` : 'Перенос...';
        barWrap.style.display = 'block';
        const pct = m.total > 0 ? Math.min(100, Math.round((m.current / m.total) * 100)) : 10;
        barFill.style.width = `${pct}%`;
      } else {
        btn.disabled = false;
        btn.style.opacity = '1';
        btnTxt.textContent = 'В Яндекс';
        barWrap.style.display = 'none';
        barFill.style.width = '100%';
      }
      break;
    }

    case 'eqState':
      st.eq = { ...st.eq, ...m };
      renderSoundControls();
      break;

    case 'windowState':
      document.getElementById('btnWinMax').textContent = m.maximized ? '❐' : '□';
      break;

    case 'seekFeedback':
      triggerSeekVisualEffect(m.targetMs || 0, m.deltaMs || 0);
      break;

    case 'focusNewPlaylist':
      setSidebarCollapsed(false, true);
      selectLeftTab(2);
      st.openedPlaylistId = null;
      renderPlaylists();
      document.getElementById('newPlForm').style.display = 'block';
      document.getElementById('newPlInput').focus();
      break;

    case 'showDrawerTab':
      showDrawerTab(Number(m.tab ?? 1));
      break;
  }
});

renderFilters();
renderStage();
postCmd('ready');
</script>
</body>
</html>
""";
}
