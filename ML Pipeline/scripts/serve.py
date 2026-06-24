from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import zipfile
from pathlib import Path
from typing import Any
from urllib.parse import quote

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from tcg_ml.analysis import analyze_games, describe, matchup_key
from tcg_ml.backfill import backfill as backfill_games_jsonl, enrich_existing_metadata
from tcg_ml.cards import CardCatalog
from tcg_ml.dataset import candidates_equivalent, choose_label_index
from tcg_ml.features import FeatureEncoder
from tcg_ml.logs import (
    ROOT_SOURCE,
    iter_decision_files,
    iter_jsonl,
    load_games,
    scan_dataset_incremental,
    usable_decision,
)
from tcg_ml.model import ActionScorer, select_device, torch_device_report
from tcg_ml.paths import add_path_args, get_paths
from tcg_ml.spec import load_spec


HTML = r"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>TCG Station ML</title>
  <style>
    :root {
      color-scheme: dark;
      --bg: #091018;
      --panel: rgba(17, 27, 38, .86);
      --panel-2: rgba(23, 36, 50, .92);
      --line: rgba(154, 178, 205, .22);
      --line-strong: rgba(95, 232, 207, .42);
      --text: #edf7fb;
      --muted: #9fb3c7;
      --primary: #38d6c0;
      --accent: #7c8cff;
      --warning: #f5bd4f;
      --danger: #f06478;
      --success: #5ee48f;
      --magenta: #e36bff;
      --orange: #ff9d5c;
      --log-bg: #071019;
      --input-bg: rgba(5, 12, 19, .86);
      --btn-bg: rgba(28, 43, 58, .95);
      --btn-bg-hover: rgba(36, 58, 76, .98);
      --body-bg:
        linear-gradient(120deg, rgba(56, 214, 192, .08), transparent 32%),
        linear-gradient(240deg, rgba(227, 107, 255, .07), transparent 34%),
        linear-gradient(180deg, #0b1119 0%, #091018 100%);
      --header-bg:
        linear-gradient(135deg, rgba(56, 214, 192, .12), rgba(124, 140, 255, .08), rgba(255, 157, 92, .06)),
        rgba(9, 16, 24, .82);
      font-family: "Segoe UI", Inter, Arial, sans-serif;
      background: var(--bg);
      color: var(--text);
    }
    :root[data-theme="light"] {
      color-scheme: light;
      --bg: #eef2f7;
      --panel: rgba(255, 255, 255, .94);
      --panel-2: rgba(244, 248, 252, .96);
      --line: rgba(40, 60, 90, .16);
      --line-strong: rgba(18, 160, 140, .5);
      --text: #14202c;
      --muted: #5a6b7d;
      --primary: #159c8d;
      --accent: #4c64d8;
      --warning: #c98a10;
      --danger: #d84a5e;
      --success: #1f9d57;
      --magenta: #a84bd6;
      --orange: #d86f22;
      --log-bg: #f3f6fa;
      --input-bg: #ffffff;
      --btn-bg: #e6ecf3;
      --btn-bg-hover: #d8e0ea;
      --body-bg:
        linear-gradient(120deg, rgba(21, 156, 141, .10), transparent 34%),
        linear-gradient(240deg, rgba(168, 75, 214, .08), transparent 36%),
        linear-gradient(180deg, #f4f7fb 0%, #eef2f7 100%);
      --header-bg:
        linear-gradient(135deg, rgba(21, 156, 141, .14), rgba(76, 100, 216, .09), rgba(216, 111, 34, .07)),
        rgba(255, 255, 255, .82);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      background: var(--body-bg);
    }
    header {
      position: relative;
      overflow: hidden;
      padding: 34px 28px 24px;
      border-bottom: 1px solid var(--line);
      background: var(--header-bg);
    }
    header:before {
      content: "";
      position: absolute;
      inset: 0;
      background-image:
        linear-gradient(rgba(255,255,255,.04) 1px, transparent 1px),
        linear-gradient(90deg, rgba(255,255,255,.04) 1px, transparent 1px);
      background-size: 42px 42px;
      mask-image: linear-gradient(90deg, rgba(0,0,0,.7), transparent 82%);
      pointer-events: none;
    }
    .hero { position: relative; max-width: 1280px; margin: 0 auto; display: grid; gap: 18px; }
    .hero-top { display: flex; justify-content: space-between; align-items: start; gap: 16px; flex-wrap: wrap; }
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 7px 10px;
      border-radius: 999px;
      background: linear-gradient(135deg, var(--primary), var(--accent));
      color: #071019;
      font-size: 12px;
      font-weight: 900;
    }
    h1 { margin: 0; font-size: 36px; line-height: 1.02; letter-spacing: 0; }
    h2 { margin: 0 0 12px; font-size: 17px; letter-spacing: 0; }
    main { max-width: 1280px; margin: 0 auto; padding: 22px 18px 40px; }
    .lead, .subtle { color: var(--muted); font-size: 14px; }
    .lead { max-width: 760px; margin: 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 10px; margin-bottom: 16px; }
    .card, .panel {
      min-width: 0;
      border: 1px solid var(--line);
      border-radius: 12px;
      background: var(--panel);
      box-shadow: 0 18px 60px rgba(0,0,0,.18);
    }
    .card { padding: 13px; }
    .card:nth-child(4n+1) { border-top-color: var(--primary); }
    .card:nth-child(4n+2) { border-top-color: var(--accent); }
    .card:nth-child(4n+3) { border-top-color: var(--magenta); }
    .card:nth-child(4n+4) { border-top-color: var(--orange); }
    .panel { padding: 16px; margin-bottom: 16px; }
    .loading-panel {
      display: flex;
      align-items: center;
      gap: 10px;
      color: var(--muted);
    }
    .loading-dot {
      width: 10px;
      height: 10px;
      border-radius: 999px;
      background: var(--primary);
      box-shadow: 0 0 0 0 rgba(67, 217, 189, .45);
      animation: pulse-dot 1.1s ease-out infinite;
      flex: 0 0 auto;
    }
    @keyframes pulse-dot {
      0% { box-shadow: 0 0 0 0 rgba(67, 217, 189, .45); opacity: 1; }
      100% { box-shadow: 0 0 0 10px rgba(67, 217, 189, 0); opacity: .55; }
    }
    .collapsible-panel { padding: 0; overflow: hidden; }
    .collapsible-panel summary {
      list-style: none;
      cursor: pointer;
      padding: 14px 16px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      font-size: 17px;
      font-weight: 900;
    }
    .collapsible-panel summary::-webkit-details-marker { display: none; }
    .collapsible-panel summary:after {
      content: "Expand";
      color: var(--muted);
      font-size: 11px;
      font-weight: 850;
      text-transform: uppercase;
    }
    .collapsible-panel[open] summary {
      border-bottom: 1px solid var(--line);
    }
    .collapsible-panel[open] summary:after { content: "Collapse"; }
    .collapsible-body { padding: 16px; }
    .label { color: var(--muted); font-size: 11px; font-weight: 800; text-transform: uppercase; }
    .value { font-size: 21px; font-weight: 900; margin-top: 5px; overflow-wrap: anywhere; }
    .row { display: flex; flex-wrap: wrap; gap: 10px; align-items: end; }
    .card.wide { grid-column: span 2; }
    .form-group { margin-bottom: 14px; }
    .form-group:last-of-type { margin-bottom: 0; }
    .group-label { display: block; margin-bottom: 6px; color: var(--muted); font-size: 11px; font-weight: 800; text-transform: uppercase; letter-spacing: .04em; }
    .form-actions { margin-top: 14px; padding-top: 14px; border-top: 1px solid var(--line); }
    .log-eta { margin-top: 8px; min-height: 18px; font-size: 13px; font-weight: 700; color: var(--accent); }
    .suggest-box { margin-top: 14px; display: none; }
    .suggest-applied { font-weight: 800; color: var(--accent); margin-bottom: 8px; font-size: 13px; }
    .suggest-note { font-size: 13px; color: var(--muted); margin: 4px 0; }
    .suggest-warn { font-size: 13px; margin: 5px 0; padding: 7px 10px; border-radius: 8px; border: 1px solid var(--line); }
    .suggest-warn.error { background: rgba(220,40,40,.12); border-color: rgba(220,40,40,.35); }
    .suggest-warn.warn { background: rgba(230,150,20,.12); border-color: rgba(230,150,20,.35); }
    .suggest-warn.info, .suggest-warn.ok { background: rgba(40,120,220,.10); }
    .stages { display: grid; grid-template-columns: repeat(6, minmax(120px, 1fr)); gap: 10px; }
    .stage {
      min-height: 76px;
      padding: 12px;
      border-radius: 10px;
      border: 1px solid var(--line);
      background: var(--panel-2);
    }
    .stage:before {
      content: attr(data-step);
      display: inline-grid;
      place-items: center;
      min-width: 24px;
      height: 20px;
      margin-bottom: 9px;
      border-radius: 999px;
      background: rgba(67,217,189,.14);
      color: var(--primary);
      font-size: 11px;
      font-weight: 950;
    }
    .stage.running { border-color: var(--line-strong); }
    .stage.done:before { content: "OK"; color: var(--success); }
    .stage:nth-child(2):before { background: rgba(124,140,255,.14); color: var(--accent); }
    .stage:nth-child(3):before { background: rgba(245,189,79,.14); color: var(--warning); }
    .stage:nth-child(4):before { background: rgba(227,107,255,.14); color: var(--magenta); }
    .stage:nth-child(5):before { background: rgba(255,157,92,.14); color: var(--orange); }
    .stage:nth-child(6):before { background: rgba(94,228,143,.14); color: var(--success); }
    .stage strong { display: block; font-size: 13px; }
    .stage span { display: block; margin-top: 4px; color: var(--muted); font-size: 11px; font-weight: 700; }
    label { display: grid; gap: 5px; color: var(--muted); font-size: 12px; font-weight: 800; text-transform: uppercase; }
    input, select {
      background: var(--input-bg);
      color: var(--text);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 9px 10px;
      min-width: 120px;
    }
    button, a.button {
      background: var(--btn-bg);
      color: var(--text);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 10px 14px;
      text-decoration: none;
      display: inline-block;
      cursor: pointer;
      min-height: 38px;
      font-weight: 850;
      font-size: 13px;
    }
    button:hover, a.button:hover { border-color: var(--line-strong); background: var(--btn-bg-hover); }
    button.primary { background: linear-gradient(135deg, var(--primary), var(--accent)); color: #071019; border-color: transparent; }
    button.secondary { background: var(--btn-bg); }
    button.danger { background: rgba(176, 58, 75, .9); border-color: rgba(255,255,255,.12); }
    button:disabled { opacity: 0.55; cursor: not-allowed; }
    pre {
      white-space: pre-wrap;
      background: var(--log-bg);
      border: 1px solid var(--line);
      border-radius: 10px;
      padding: 14px;
      overflow: auto;
      min-height: 70px;
      max-height: 130px;
      color: var(--text);
      font: 12px/1.55 Consolas, "Cascadia Mono", monospace;
    }
    #log { min-height: 240px; max-height: 460px; resize: vertical; }
    .field-hint { display: block; margin-top: 4px; font-size: 11px; }
    .status { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); }
    .dot { width: 9px; height: 9px; border-radius: 50%; background: #6b7280; display: inline-block; }
    .dot.on { background: var(--success); }
    .charts { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; margin-top: 12px; }
    .charts > div { min-width: 0; }
    .chart-wrap { position: relative; height: 200px; }
    .chart-wrap canvas { max-width: 100%; }
    .chart-empty { color: var(--muted); font-size: 13px; text-align: center; padding: 60px 0; }
    .theme-toggle {
      min-height: 34px;
      padding: 7px 14px;
      font-size: 12px;
      font-weight: 850;
      border-radius: 999px;
    }
    .tabs {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-bottom: 16px;
      border-bottom: 1px solid var(--line);
      padding-bottom: 2px;
    }
    .tab-btn {
      background: transparent;
      border: 1px solid transparent;
      border-bottom: none;
      border-radius: 10px 10px 0 0;
      color: var(--muted);
      padding: 10px 16px;
      cursor: pointer;
      font-weight: 850;
      font-size: 13px;
      min-height: 38px;
    }
    .tab-btn:hover { color: var(--text); background: var(--btn-bg); }
    .tab-btn.active {
      color: var(--text);
      background: var(--panel);
      border-color: var(--line);
      border-bottom: 1px solid var(--panel);
      margin-bottom: -2px;
    }
    .tab-panel { display: none; }
    .tab-panel.active { display: block; }
    .activity-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 12px; }
    .activity-head h2 { margin: 0; }
    /* Dock the Process Log as a slide-out panel pinned to the right edge, so it's reachable
       from any scroll position without jumping to the bottom of the page. */
    #activity.docked {
      position: fixed; top: 0; right: 0; bottom: 0; z-index: 60;
      width: min(560px, 42vw); height: 100vh; margin: 0;
      border-radius: 0; border-left: 1px solid var(--line);
      box-shadow: -10px 0 28px rgba(0,0,0,.30);
      display: flex; flex-direction: column; overflow: hidden;
      animation: dock-in .18s ease-out;
    }
    @keyframes dock-in { from { transform: translateX(24px); opacity: .4; } to { transform: none; opacity: 1; } }
    #activity.docked .activity-head { margin-bottom: 8px; }
    #activity.docked #log, #activity.docked #raw { flex: 1 1 auto; max-height: none; min-height: 0; resize: none; }
    body.has-docked-log { padding-right: min(560px, 42vw); }
    body.has-docked-log .stages { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    body.has-docked-log .grid { grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); }
    body.has-docked-log .charts { grid-template-columns: minmax(0, 1fr); }
    body.has-docked-log .replay-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    body.has-docked-log .advisor-event { grid-template-columns: 74px 70px minmax(0, 1fr); }
    body.has-docked-log .models, body.has-docked-log .replay-step { overflow-x: auto; }
    @media (max-width: 980px) {
      #activity.docked { width: 100vw; }
      body.has-docked-log { padding-right: 0; }
      body.has-docked-log .stages { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      body.has-docked-log .advisor-event { grid-template-columns: 1fr; }
    }
    .progress { height: 7px; border-radius: 999px; background: var(--btn-bg); overflow: hidden; display: none; margin-top: 12px; }
    .progress.active { display: block; }
    .progress > i { display: block; height: 100%; width: 38%; border-radius: 999px; background: linear-gradient(90deg, var(--primary), var(--accent)); animation: progressSlide 1.05s infinite ease-in-out; }
    @keyframes progressSlide { 0% { transform: translateX(-110%); } 100% { transform: translateX(290%); } }
    .progress-note { color: var(--muted); font-size: 12px; font-weight: 700; margin-top: 7px; min-height: 16px; }
    .inline-check { display: inline-flex; flex-direction: row; align-items: center; gap: 7px; text-transform: none; font-size: 12px; color: var(--muted); cursor: pointer; }
    .inline-check input { min-width: 0; width: 16px; height: 16px; }
    .run-picker { display: flex; flex-wrap: wrap; gap: 8px 12px; margin-top: 10px; }
    .run-picker .inline-check { font-family: Consolas, "Cascadia Mono", monospace; }
    .run-picker:empty { display: none; }
    .run-chip {
      display: inline-flex;
      align-items: center;
      gap: 7px;
      padding: 6px 8px;
      border: 1px solid var(--line);
      border-radius: 999px;
      background: var(--panel-2);
    }
    .run-chip button {
      min-height: 22px;
      padding: 2px 7px;
      border-radius: 999px;
      font-size: 11px;
      line-height: 1;
    }
    textarea {
      background: var(--input-bg);
      color: var(--text);
      border: 1px solid var(--line);
      border-radius: 8px;
      padding: 10px;
      width: 100%;
      resize: vertical;
      font: 12px/1.5 Consolas, "Cascadia Mono", monospace;
    }
    .models table, .replay-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .models th, .models td, .replay-table th, .replay-table td {
      text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--line);
    }
    .models th, .replay-table th { color: var(--muted); font-size: 11px; text-transform: uppercase; font-weight: 800; }
    .stat-sort {
      appearance: none;
      border: 0;
      background: transparent;
      color: inherit;
      padding: 0;
      min-height: auto;
      font: inherit;
      text-transform: inherit;
      cursor: pointer;
    }
    .stat-sort:hover { color: var(--text); }
    .stat-sort .sort-indicator { display: inline-block; min-width: 12px; margin-left: 4px; color: var(--muted); }
    .models td .mono, .replay-table td .mono { font-family: Consolas, "Cascadia Mono", monospace; font-size: 12px; overflow-wrap: anywhere; }
    /* Replay explorer */
    .replay-summary { margin-top: 12px; }
    .replay-cards { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; }
    .replay-nav { align-items: center; gap: 10px; margin-bottom: 12px; }
    .replay-head { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 10px; }
    .chip { display: inline-block; padding: 2px 9px; border-radius: 999px; border: 1px solid var(--line); background: var(--panel-2); font-size: 12px; font-weight: 700; }
    .chip.ok { color: #19c37d; border-color: rgba(25,195,125,.5); }
    .chip.bad { color: #ef5b6b; border-color: rgba(239,91,107,.5); }
    .tag { display: inline-block; padding: 1px 7px; border-radius: 6px; font-size: 10px; font-weight: 800; text-transform: uppercase; margin-right: 4px; }
    .tag.expert { background: rgba(99,140,255,.18); color: #6c8bff; }
    .tag.model { background: rgba(67,217,189,.18); color: #2bbfa6; }
    .tag.train { background: rgba(67,217,189,.18); color: var(--primary); }
    .tag.finetune { background: rgba(227,107,255,.18); color: var(--magenta); }
    .tag.missing { background: rgba(239,91,107,.16); color: var(--danger); }
    .replay-table tr.blocked { opacity: .5; }
    .replay-table tr.is-expert { background: rgba(99,140,255,.08); }
    .replay-table tr.is-model { box-shadow: inset 2px 0 0 #2bbfa6; }
    .board-state {
      margin-top: 14px;
      padding: 12px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: rgba(8, 16, 25, .46);
    }
    :root[data-theme="light"] .board-state { background: rgba(244, 248, 252, .84); }
    .board-state h3 { margin: 0 0 10px; font-size: 13px; }
    .board-grid { display: grid; gap: 10px; }
    .board-player {
      display: grid;
      grid-template-columns: minmax(120px, 150px) minmax(130px, 180px) 1fr;
      gap: 10px;
      align-items: stretch;
    }
    .board-side-meta, .pokemon-card, .bench-slot {
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel-2);
    }
    .board-side-meta { padding: 9px; display: grid; align-content: start; gap: 5px; }
    .board-side-title { font-weight: 900; }
    .board-side-title.acting { color: var(--primary); }
    .board-kv { color: var(--muted); font-size: 11px; }
    .pokemon-card { padding: 9px; min-height: 86px; position: relative; overflow: hidden; }
    .pokemon-card.active { border-color: var(--line-strong); box-shadow: inset 0 0 0 1px rgba(95, 232, 207, .16); }
    .pokemon-name { font-weight: 900; overflow-wrap: anywhere; }
    .pokemon-sub { color: var(--muted); font-size: 11px; margin-top: 3px; }
    .hp-bar {
      height: 7px;
      margin-top: 7px;
      border-radius: 999px;
      overflow: hidden;
      background: rgba(127, 146, 166, .24);
    }
    .hp-bar > i { display: block; height: 100%; background: linear-gradient(90deg, var(--primary), var(--success)); }
    .hp-bar.danger > i { background: var(--danger); }
    .energy-pills, .status-pills { display: flex; flex-wrap: wrap; gap: 4px; margin-top: 7px; }
    .energy-pill, .status-pill {
      display: inline-block;
      padding: 1px 6px;
      border-radius: 999px;
      border: 1px solid var(--line);
      font-size: 10px;
      color: var(--muted);
    }
    .status-pill.bad { color: var(--danger); border-color: rgba(240,100,120,.45); }
    .bench-row { display: grid; gap: 8px; }
    .bench-slot { min-height: 86px; }
    .bench-slot.empty { display: grid; place-items: center; color: var(--muted); font-size: 12px; border-style: dashed; }
    @media (max-width: 900px) {
      .board-player { grid-template-columns: 1fr; }
      .bench-row { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    }
    .reason-help { position: relative; display: inline-flex; align-items: center; gap: 5px; }
    .info-dot {
      display: inline-grid;
      place-items: center;
      width: 16px;
      height: 16px;
      min-height: 16px;
      padding: 0;
      border-radius: 999px;
      border: 1px solid var(--line);
      background: var(--panel-2);
      color: var(--muted);
      font-size: 11px;
      font-weight: 900;
      line-height: 1;
      text-transform: none;
      cursor: pointer;
    }
    .info-dot:hover, .info-dot.active { border-color: var(--primary); color: var(--primary); }
    .reason-popover {
      position: absolute;
      top: 24px;
      right: 0;
      z-index: 30;
      width: min(520px, 82vw);
      max-height: 62vh;
      overflow: auto;
      padding: 12px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
      box-shadow: 0 18px 55px rgba(0, 0, 0, .32);
      color: var(--text);
      text-transform: none;
      font-size: 12px;
      line-height: 1.35;
    }
    .reason-popover h3 { margin: 0 0 8px; font-size: 13px; color: var(--text); }
    .reason-popover p { margin: 0 0 10px; color: var(--muted); font-weight: 500; }
    .reason-popover dl { margin: 0; display: grid; gap: 8px; }
    .reason-popover dt { color: var(--text); font-family: Consolas, "Cascadia Mono", monospace; font-weight: 800; }
    .reason-popover dd { margin: 2px 0 0; color: var(--muted); font-weight: 500; }
    #replay-diff-btn.active, #replay-hidemeta-btn.active, #dock-log-btn.active { background: var(--primary); color: #06231d; border-color: var(--primary); }
    .models .loaded-row { background: rgba(67, 217, 189, .10); }
    .models button { padding: 6px 12px; min-height: 30px; font-size: 12px; }
    .advisor-feed { display: grid; gap: 8px; margin-top: 12px; }
    .advisor-empty { color: var(--muted); font-size: 13px; }
    .advisor-event {
      display: grid;
      grid-template-columns: 90px 82px 1fr;
      gap: 10px;
      align-items: start;
      padding: 10px 12px;
      border: 1px solid var(--line);
      border-left: 4px solid var(--accent);
      border-radius: 8px;
      background: var(--panel-2);
    }
    .advisor-event.ml { border-left-color: var(--primary); }
    .advisor-event.llm { border-left-color: var(--magenta); }
    .advisor-time, .advisor-stage { color: var(--muted); font-size: 11px; font-weight: 850; text-transform: uppercase; }
    .advisor-message { font-size: 13px; line-height: 1.45; overflow-wrap: anywhere; }
    .advisor-meta { margin-top: 3px; color: var(--muted); font-size: 11px; font-family: Consolas, "Cascadia Mono", monospace; }
    .stat-table { width: 100%; border-collapse: collapse; font-size: 13px; }
    .stat-table th, .stat-table td { padding: 6px 9px; text-align: right; border-bottom: 1px solid var(--line); }
    .stat-table th:first-child, .stat-table td:first-child { text-align: left; font-weight: 800; }
    .stat-table thead th { color: var(--muted); font-size: 10px; text-transform: uppercase; letter-spacing: .04em; }
    .stat-table td { font-family: Consolas, "Cascadia Mono", monospace; }
    .corr-grid { border-collapse: collapse; font-size: 12px; }
    .corr-grid th, .corr-grid td { padding: 5px 8px; text-align: center; border: 1px solid var(--line); }
    .corr-grid th { color: var(--muted); font-size: 10px; text-transform: uppercase; }
    .corr-grid td { font-family: Consolas, "Cascadia Mono", monospace; color: #061018; font-weight: 800; }
    .hist { display: flex; align-items: flex-end; gap: 3px; height: 130px; padding: 6px 2px 0; }
    .hist .bar { flex: 1; background: linear-gradient(180deg, var(--primary), rgba(95,232,207,.25));
      border-radius: 4px 4px 0 0; min-height: 2px; position: relative; }
    .hist .bar span { position: absolute; top: -15px; left: 0; right: 0; text-align: center;
      font-size: 9px; color: var(--muted); }
    .hist-axis { display: flex; justify-content: space-between; font-size: 10px; color: var(--muted); margin-top: 4px; }
    .test-card { padding: 11px 13px; border-radius: 10px; border: 1px solid var(--line);
      background: var(--panel-2); border-left: 3px solid var(--line-strong); margin-top: 10px; }
    .test-card.sig { border-left-color: var(--magenta); }
    .test-card.nsig { border-left-color: var(--primary); }
    .test-verdict { font-weight: 900; font-size: 14px; margin-top: 4px; }
    .test-stats { color: var(--muted); font-size: 12px; font-family: Consolas, "Cascadia Mono", monospace; margin-top: 4px; }
    .wr-row { display: grid; grid-template-columns: 130px 1fr 120px; align-items: center; gap: 10px; margin: 6px 0; }
    .wr-bar-track { background: var(--panel-2); border-radius: 6px; height: 18px; overflow: hidden; border: 1px solid var(--line); }
    .wr-bar-fill { height: 100%; background: linear-gradient(90deg, var(--primary), var(--magenta)); }
    .analysis-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 14px; }
    .health { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 8px; }
    .health .hcell { padding: 9px 11px; border-radius: 9px; border: 1px solid var(--line); background: var(--panel-2); }
    .health .hk { color: var(--muted); font-size: 10px; font-weight: 800; text-transform: uppercase; }
    .health .hv { font-size: 17px; font-weight: 900; margin-top: 3px; }
    .health .hv.warn { color: var(--warning); }
    .health .hv.bad { color: var(--danger); }
    .card-usage-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; }
    .usage-hero {
      display: grid;
      grid-template-columns: 74px minmax(0, 1fr);
      gap: 12px;
      align-items: start;
      min-height: 126px;
      padding: 12px;
      border: 1px solid var(--line);
      border-radius: 10px;
      background: var(--panel-2);
    }
    .usage-art {
      width: 74px;
      aspect-ratio: 3 / 4;
      border-radius: 8px;
      border: 1px solid var(--line);
      background: linear-gradient(135deg, rgba(56,214,192,.18), rgba(124,140,255,.14));
      object-fit: cover;
      display: grid;
      place-items: center;
      color: var(--muted);
      font-size: 18px;
      font-weight: 950;
      overflow: hidden;
    }
    .usage-art img { width: 100%; height: 100%; object-fit: cover; display: block; }
    .usage-title { font-size: 15px; font-weight: 950; margin-top: 2px; overflow-wrap: anywhere; }
    .usage-metric { color: var(--primary); font-size: 22px; font-weight: 950; margin-top: 3px; }
    .usage-subtitle { color: var(--muted); font-size: 11px; font-weight: 800; text-transform: uppercase; margin-top: 2px; }
    .usage-desc { color: var(--muted); font-size: 12px; line-height: 1.35; margin-top: 7px; }
    .usage-rank { display: grid; gap: 7px; margin-top: 10px; }
    .usage-rank-row {
      display: grid;
      grid-template-columns: 28px minmax(0, 1fr) 58px;
      gap: 9px;
      align-items: center;
      padding: 7px 9px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel-2);
      font-size: 12px;
    }
    .usage-rank-row strong { overflow-wrap: anywhere; }
    .usage-rank-count { text-align: right; color: var(--primary); font-family: Consolas, "Cascadia Mono", monospace; font-weight: 900; }
    .usage-note { margin-top: 10px; color: var(--muted); font-size: 11px; line-height: 1.45; }
    .modal-backdrop {
      position: fixed;
      inset: 0;
      display: none;
      place-items: center;
      padding: 20px;
      background: rgba(4, 10, 16, .66);
      backdrop-filter: blur(8px);
      z-index: 1000;
    }
    .modal-backdrop.open { display: grid; }
    .modal-card {
      width: min(560px, 100%);
      border: 1px solid var(--line);
      border-radius: 12px;
      background: var(--panel);
      box-shadow: 0 28px 90px rgba(0,0,0,.42);
      padding: 18px;
    }
    .modal-card h2 { margin: 0 0 8px; }
    .modal-actions {
      display: flex;
      justify-content: flex-end;
      gap: 10px;
      margin-top: 18px;
      flex-wrap: wrap;
    }
    @media (max-width: 980px) {
      .stages { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .charts { grid-template-columns: minmax(0, 1fr); }
      .replay-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
    }
  </style>
  <script src="https://cdn.jsdelivr.net/npm/chart.js@4/dist/chart.umd.min.js"></script>
</head>
<body>
  <header>
    <div class="hero">
      <div class="hero-top">
        <div>
          <h1>TCG Station ML</h1>
        </div>
        <div class="row" style="align-items:center; gap:14px;">
          <div class="status"><span id="api-dot" class="dot on"></span><span>API on localhost:8000</span></div>
          <button id="theme-btn" class="theme-toggle" onclick="toggleTheme()">Light mode</button>
        </div>
      </div>
      <div class="stages">
        <div id="stage-setup" class="stage" data-step="1"><strong>Environment</strong><span id="stage-setup-note">stopped</span></div>
        <div id="stage-dataset" class="stage" data-step="2"><strong>Dataset</strong><span id="stage-dataset-note">not scanned</span></div>
        <div id="stage-train" class="stage" data-step="3"><strong>Training</strong><span id="stage-train-note">stopped</span></div>
        <div id="stage-eval" class="stage" data-step="4"><strong>Evaluation</strong><span id="stage-eval-note">stopped</span></div>
        <div id="stage-model" class="stage" data-step="5"><strong>Model</strong><span id="stage-model-note">not loaded</span></div>
        <div id="stage-metrics" class="stage" data-step="6"><strong>Metrics</strong><span id="stage-metrics-note">no data</span></div>
      </div>
    </div>
  </header>
  <main>
    <section class="grid" id="cards"></section>

    <nav class="tabs" id="tabs">
      <button class="tab-btn active" data-tab="environment" onclick="showTab('environment')">Environment</button>
      <button class="tab-btn" data-tab="training" onclick="showTab('training')">Training</button>
      <button class="tab-btn" data-tab="model" onclick="showTab('model')">Model &amp; Eval</button>
      <button class="tab-btn" data-tab="replay" onclick="showTab('replay')">Replay</button>
      <button class="tab-btn" data-tab="advisors" onclick="showTab('advisors')">Advisors</button>
      <button class="tab-btn" data-tab="analysis" onclick="showTab('analysis')">Analysis</button>
    </nav>

    <section class="tab-panel active" data-tab="environment">
      <div class="panel">
        <h2>Environment</h2>
        <div class="row">
          <label>Setup profile
            <select id="setup-profile">
              <option value="auto">Auto-detect</option>
              <option value="gpu">GPU / CUDA</option>
              <option value="cpu">CPU only</option>
            </select>
          </label>
          <button onclick="checkEnvironment()">Check environment</button>
          <button class="primary" onclick="startSetup()">Install / repair dependencies</button>
          <button class="danger" id="setup-stop" onclick="stopSetup()" style="display:none">Stop setup</button>
        </div>
      </div>

      <div class="panel">
        <h2>Dataset</h2>
        <div class="row">
          <button class="primary" id="scan-btn" onclick="scanDataset()">Scan dataset</button>
          <button onclick="refresh()">Refresh status</button>
        </div>
        <details class="panel collapsible-panel" style="margin:14px 0 0">
          <summary>Synchronize</summary>
          <div class="collapsible-body">
            <div class="row">
              <button id="fetch-logs-btn" onclick="fetchServerLogs()" title="Pull decision logs from an optional configured source into the local ML logs. Files that already exist locally are skipped; games.jsonl rows are merged by game_id.">⬇ Fetch logs from server</button>
              <button id="download-patch-btn" onclick="downloadJsonPatch()" title="Mirror latest card and deck JSONs from the configured ProjektJSONs folder into local Cards/ and Decks/. Current Cards/ and Decks/ are archived first.">📥 Download patch</button>
            </div>
            <div class="row" style="margin-top:8px; gap:6px; flex-direction:column; align-items:stretch">
              <input id="mirror-path" type="text" placeholder="Device 1 — path to logs copy (build root, Logs Export, …/ML, or Decisions)" spellcheck="false" style="width:100%; box-sizing:border-box">
              <input id="mirror-path-2" type="text" placeholder="Device 2 — path to logs copy (build root, Logs Export, …/ML, or Decisions)" spellcheck="false" style="width:100%; box-sizing:border-box">
              <select id="mirror-direction" title="Pick how logs flow. Two-way: additive both ways (default). Pull: only copy the chosen device(s) INTO local. Push: only send local OUT to the device(s). For Pull/Push you can leave a device field empty to use just one." style="width:100%; box-sizing:border-box">
                <option value="two-way">Two-way (additive, both devices)</option>
                <option value="pull">Pull — mirror device(s) → local only</option>
                <option value="push">Push — mirror local → device(s) only</option>
              </select>
            </div>
            <div class="row" style="margin-top:6px">
              <button id="mirror-decisions-btn" onclick="syncDecisionLogsMirror()" title="Sync the local ML logs with the logs copies you point at (one per device). Direction is chosen above: two-way additive (default), pull device→local, or push local→device. Decision .jsonl files are copied without deleting, and games.jsonl is merged by game_id.">↔ Sync ML logs mirror</button>
            </div>
            <p class="subtle" style="margin:10px 0 0">Sync ML logs mirror works on the path to each device's logs copy. Paste any level — the build root, the <span class="mono">Logs Export</span> or <span class="mono">Logs Export/ML</span> folder, or the <span class="mono">Decisions</span> folder itself — and the subfolders are detected automatically. The <b>direction</b> selector chooses the flow: <b>Two-way</b> copies missing logs every way (needs both devices); <b>Pull</b> mirrors a chosen device <em>into</em> local only; <b>Push</b> sends local <em>out</em> to a device only. For Pull/Push you can fill just one device field. <span class="mono">games.jsonl</span> is merged by <span class="mono">game_id</span> in the matching direction. Set <span class="mono">TCG_DECISIONS_MIRROR_DIR</span> (paths separated by <span class="mono">;</span>) to prefill the device fields from the environment.</p>
            <p class="subtle" style="margin:8px 0 0">Download patch mirrors latest card/deck JSONs from ProjektJSONs into local <span class="mono">Cards/</span> and <span class="mono">Decks/</span>. It archives current Cards and Decks first, then asks whether to archive training logs too.</p>
          </div>
        </details>
        <details class="panel collapsible-panel" style="margin:14px 0 0" id="paths-panel">
          <summary>ML data paths</summary>
          <div class="collapsible-body">
            <p class="subtle" style="margin:0 0 8px">Point the pipeline at a different build's <span class="mono">Logs Export/ML</span> folder (e.g. a macOS or a Windows build). Paste either the <span class="mono">Logs Export/ML</span> folder itself or the build root that contains it. Applies live to Scan / Train / Evaluate and persists across restarts.</p>
            <input id="path-logs" type="text" placeholder="/path/to/Builds/macOS/Logs Export/ML" spellcheck="false" oninput="this.dataset.dirty='1'" style="width:100%; box-sizing:border-box">
            <div class="row" style="margin-top:6px">
              <input id="path-cards" type="text" placeholder="Cards/ override (optional)" spellcheck="false" style="flex:1">
              <input id="path-decks" type="text" placeholder="Decks/ override (optional)" spellcheck="false" style="flex:1">
            </div>
            <div class="row" style="margin-top:8px">
              <button class="secondary" onclick="checkPaths()" title="Validate the typed ML logs path and report how many decision files it contains, without applying it.">Check</button>
              <button class="primary" onclick="applyPaths()" title="Switch the pipeline to these paths now (scan/train/evaluate use them) and save the choice.">Apply</button>
              <button onclick="resetPaths()" title="Clear the overrides and fall back to the startup (CLI/env) defaults.">Reset to default</button>
            </div>
            <p class="subtle mono" id="paths-current" style="margin:10px 0 0"></p>
            <p class="progress-note" id="paths-note"></p>
          </div>
        </details>
        <div class="progress" id="scan-progress"><i></i></div>
        <div class="progress-note" id="scan-note"></div>
      </div>

      <div class="panel">
        <h2>Dataset Breakdown</h2>
        <div id="dataset-health" class="health"></div>
        <div class="subtle" style="margin-top:12px">All sources combined</div>
        <div class="chart-wrap" style="height:260px; margin-top:4px;"><canvas id="chart-dataset"></canvas></div>
        <div id="dataset-empty" class="chart-empty">Scan the dataset to see the per-category distribution.</div>
        <div id="dataset-source-charts"></div>
      </div>
    </section>

    <section class="tab-panel" data-tab="training">
      <details class="panel collapsible-panel" open>
        <summary>Training</summary>
        <div class="collapsible-body">
        <div class="form-group">
          <span class="group-label">Data</span>
          <div class="row">
            <label title="Train on this many randomly-sampled games (whole decision logs). Enter 0 to use ALL available games. Validation holds out a fraction of these games, so no game is split across train/val.">Max games (random)
              <input id="train-max" type="number" value="0" min="0" oninput="updateGamesHint()">
              <span id="train-games-avail" class="subtle field-hint">0 = all games</span>
            </label>
            <label title="Fraction of the selected games held out for validation (game-level split, no leakage). e.g. 0.1 = 10% of games.">Val ratio
              <input id="train-valratio" type="number" value="0.1" min="0" max="0.9" step="0.05">
            </label>
            <label title="Random seed for game sampling and weight init. Same seed + same data = reproducible run.">Seed
              <input id="train-seed" type="number" value="1234" min="0">
            </label>
            <label class="check" title="Train only on decisions made by the player who WON each game. Requires games.jsonl winner metadata; games without a recorded winner are skipped entirely.">
              <input id="train-winners-only" type="checkbox" onchange="updateGamesHint()"> Winners only
            </label>
          </div>
        </div>
        <div class="form-group">
          <span class="group-label" title="Train only on decisions made by an AlgorithmBrain running the selected profiles (reads games.jsonl brain_a/brain_b). No boxes checked = no filter (any profile). Use to train one model per profile or clone a specific expert when the dataset mixes profiles.">Profile filter</span>
          <div class="row">
            <label class="check"><input type="checkbox" class="train-profile-cb" value="Standard"> Standard</label>
            <label class="check"><input type="checkbox" class="train-profile-cb" value="Ramp"> Ramp</label>
            <label class="check"><input type="checkbox" class="train-profile-cb" value="TempoAggro"> TempoAggro</label>
            <label class="check"><input type="checkbox" class="train-profile-cb" value="ControlStatus"> ControlStatus</label>
            <label class="check"><input type="checkbox" class="train-profile-cb" value="HealStall"> HealStall</label>
            <span class="muted">none checked = any</span>
          </div>
        </div>
        <div class="form-group">
          <span class="group-label">Data sources</span>
          <div id="train-sources" class="row" title="Which decision-log contexts to train on. 'benchmark' = benchmark runs; 'interactive/<matchup>' = watchable games grouped by player types; 'legacy' = older files in the Decisions/ root. Supported brains using the shared action pipeline can contribute training decisions. Untick all to fall back to the backend default (every non-legacy source).">
            <span class="muted">loading sources…</span>
          </div>
        </div>
        <div class="form-group">
          <span class="group-label">Optimization</span>
          <div class="row">
            <label>Epochs
              <input id="train-epochs" type="number" value="3" min="1">
            </label>
            <label title="Learning rate for the Adam optimizer. Default 1e-4 for fresh training. Use 1e-5 or lower for fine-tuning.">Learning rate
              <input id="train-lr" type="number" value="0.0001" min="0" step="0.00001">
            </label>
            <label title="Accumulate gradients over N decisions before optimizer.step(). 1 = per-decision SGD (default). 16+ gives a smoother, larger effective batch.">Grad accum
              <input id="train-grad-accum" type="number" value="1" min="1">
            </label>
            <label title="Early stopping: stop after this many CONSECUTIVE epochs without an improvement in validation loss. The model on disk is always the best epoch (lowest val_loss), never a later one — so a higher patience only costs extra training time, it never keeps a worse model. 0 disables early stopping and trains the full epoch count. Default 4: val_loss on a small val set is noisy, so one bad epoch must not stop training.">Early stop patience (epochs)
              <input id="train-patience" type="number" value="4" min="0">
            </label>
          </div>
        </div>
        <div class="form-group">
          <span class="group-label">Runtime</span>
          <div class="row">
            <label>Device
              <select id="train-device">
                <option value="auto">auto</option>
                <option value="cuda">cuda</option>
                <option value="mps">mps</option>
                <option value="cpu">cpu</option>
              </select>
            </label>
            <label title="Print one progress line every N decisions while loading and every N steps while training. Higher = fewer log lines, lower = more frequent updates.">Progress line every (steps)
              <input id="train-log-every" type="number" value="1000" min="1">
            </label>
            <!-- TurnMeta logging is disabled in AlgorithmBrain (EnableTurnMetaLogging=false), so this
                 control is hidden. Kept in the DOM (display:none) so the form-save/restore JS still works.
                 Remove the inline style to bring it back if TurnMeta logging is ever re-enabled. -->
            <label class="check" style="display:none" title="Include TurnMeta records — one cross-category decision per turn showing which action TYPE the bot chose (Attack vs AttachEnergy vs PlayBasic, etc.). Has no effect until the benchmark logs TurnMeta rows; off by default so normal per-category training is unaffected.">
              <input id="train-turn-meta" type="checkbox"> Include TurnMeta
            </label>
          </div>
        </div>
        <div class="row form-actions">
          <button class="primary" id="train-start" onclick="startTraining()">Start training</button>
          <button class="secondary" id="train-suggest-btn" onclick="suggestTrainParams()" title="Fill the form with parameters derived from the scanned dataset (games, decisions, category balance) and flag data-quality issues.">✨ Suggest parameters</button>
          <button class="danger" id="train-stop" onclick="stopTraining()" style="display:none">Stop training</button>
        </div>
        <div id="train-suggest" class="suggest-box"></div>
        </div>
      </details>

      <details class="panel collapsible-panel">
        <summary>Fine-tune from checkpoint</summary>
        <div class="collapsible-body">
        <p class="subtle" style="margin:0 0 14px">Load weights from an existing model and continue training — useful for specializing a pre-trained model. Use a lower learning rate (e.g. 1e-5) so the pre-trained weights are not overwritten aggressively.</p>
        <div class="form-group">
          <span class="group-label">Source</span>
          <div class="row">
            <label>Start from model
              <select id="ft-from" style="font-family:monospace;font-size:12px">
                <option value="latest">latest</option>
              </select>
              <button class="secondary" style="margin-top:4px;font-size:11px;padding:3px 8px" onclick="refreshFtModelList()">↺ Refresh</button>
            </label>
          </div>
        </div>
        <div class="form-group">
          <span class="group-label">Data</span>
          <div class="row">
            <label title="Fine-tune on this many randomly-sampled games. 0 = all games. Kept separate from Training Max games so a small first stage does not accidentally limit the fine-tune stage.">Max games
              <input id="ft-max" type="number" value="0" min="0">
            </label>
            <label class="check" title="Fine-tune only on decisions made by the player who won.">
              <input id="ft-winners-only" type="checkbox" checked> Winners only
            </label>
          </div>
        </div>
        <div class="form-group">
          <span class="group-label">Optimization</span>
          <div class="row">
            <label>Epochs
              <input id="ft-epochs" type="number" value="5" min="1">
            </label>
            <label title="Learning rate for fine-tuning. Typically 10× lower than fresh training so pre-trained weights shift gently.">Learning rate
              <input id="ft-lr" type="number" value="0.00001" min="0" step="0.000001">
            </label>
            <label title="Accumulate gradients over N decisions.">Grad accum
              <input id="ft-grad-accum" type="number" value="1" min="1">
            </label>
          </div>
        </div>
        <div class="row form-actions">
          <button class="primary" id="ft-start" onclick="startFinetune()">Start fine-tuning</button>
          <button class="danger" id="ft-stop" onclick="stopTraining()" style="display:none">Stop</button>
        </div>
        </div>
      </details>

      <details class="panel collapsible-panel">
        <summary>Two-stage training (All → Winners)</summary>
        <div class="collapsible-body">
        <p class="subtle" style="margin:0 0 14px">Runs both stages in one click, sharing a single Process Log. <strong>Stage 1</strong> pre-trains on <em>all</em> decisions using the <em>Training</em> settings above (forced to all decisions). <strong>Stage 2</strong> immediately fine-tunes the Stage 1 model using the <em>Fine-tune</em> settings above (including its Max games). There are no separate settings here.</p>
        <div class="row form-actions" style="margin-top:0;padding-top:0;border-top:none">
          <button class="primary" id="cur-start" onclick="startCurriculum()">Run two-stage training</button>
          <button class="danger" id="cur-stop" onclick="stopCurriculum()" style="display:none">Stop</button>
          <span id="cur-stage-label" class="subtle" style="align-self:center;font-size:12px"></span>
        </div>
        </div>
      </details>

      <div class="panel">
        <h2>Metrics</h2>
        <div class="row">
          <button class="secondary" onclick="loadRunPicker(); loadMetrics();">Refresh charts</button>
          <button class="secondary" onclick="clearRunSelection()">Show latest only</button>
          <span class="subtle">Tick runs to compare. None ticked = latest run only.</span>
        </div>
        <div id="run-picker" class="run-picker"></div>
        <div class="charts">
          <div>
            <div class="label">Loss</div>
            <div class="chart-wrap"><canvas id="chart-loss"></canvas></div>
          </div>
          <div>
            <div class="label">Accuracy</div>
            <div class="chart-wrap"><canvas id="chart-acc"></canvas></div>
          </div>
        </div>
        <div id="chart-empty" class="chart-empty" style="display:none">No training runs found.</div>
      </div>
    </section>

    <section class="tab-panel" data-tab="model">
      <div class="panel">
        <h2>Models</h2>
        <div class="row">
          <button class="secondary" onclick="loadModels()">Refresh list</button>
          <button class="primary" onclick="loadLatest()">Load latest</button>
          <button onclick="startEvaluation()">Evaluate latest</button>
          <button class="danger" id="eval-stop" onclick="stopEvaluation()" style="display:none">Stop evaluation</button>
        </div>
        <div id="models-table" class="models"><span class="subtle">No models listed yet.</span></div>
      </div>

    </section>

    <section class="tab-panel" data-tab="replay">
      <div class="panel">
        <h2>Replay</h2>
        <div class="row">
          <label title="Filter the game list by who won. Useful for studying where the model diverges from the expert in games the agent lost.">Winner
            <select id="replay-winner" onchange="renderGameOptions()"><option value="">all</option></select>
          </label>
          <select id="replay-game" onchange="loadReplayGame(this.value)"><option value="">Loading games…</option></select>
          <button class="secondary" onclick="loadReplayGames()">Refresh list</button>
          <span id="replay-model" class="subtle"></span>
        </div>
        <div id="replay-summary" class="replay-summary"></div>
      </div>

      <div class="panel" id="replay-stage" style="display:none">
        <div class="row replay-nav">
          <button class="secondary" onclick="replayStep(-1)">&#8592; Prev</button>
          <input type="range" id="replay-slider" min="0" max="0" value="0" oninput="replayGoto(+this.value)" style="flex:1">
          <button class="secondary" onclick="replayStep(1)">Next &#8594;</button>
          <select id="replay-player" onchange="setReplayPlayer(this.value)" title="Filter steps by the acting player">
            <option value="">Both players</option>
            <option value="1">Player 1</option>
            <option value="2">Player 2</option>
          </select>
          <button class="secondary" id="replay-diff-btn" onclick="toggleDiffOnly()">Only disagreements</button>
          <span id="replay-diff-note" class="subtle" style="align-self:center"></span>
          <!-- TurnMeta logging is disabled (AlgorithmBrain EnableTurnMetaLogging=false), so this toggle
               is hidden. Kept in the DOM (display:none) so the replay JS that references it still works.
               Remove the inline style to bring it back if TurnMeta logging is ever re-enabled. -->
          <button class="secondary" id="replay-hidemeta-btn" style="display:none" onclick="toggleHideTurnMeta()" title="Hide the synthetic TurnMeta steps (one cross-category summary row per turn) so the slider walks only the real per-category decisions.">Hide TurnMeta</button>
        </div>
        <div id="replay-step" class="replay-step"></div>
      </div>
    </section>

    <section class="tab-panel" data-tab="advisors">
      <div class="panel">
        <h2>API Model</h2>
        <div class="row" style="align-items:center; gap:10px; flex-wrap:wrap">
          <label for="advisor-model-select">Model served on <span class="mono">/predict</span></label>
          <select id="advisor-model-select" onchange="setApiModel(this.value)" style="min-width:280px"><option value="">Loading models…</option></select>
          <button class="secondary" onclick="loadAdvisorModelPicker()">Refresh list</button>
          <span id="advisor-model-status" class="subtle"></span>
        </div>
      </div>
      <div class="panel">
        <h2>Advisor Activity</h2>
        <div class="row">
          <button class="secondary" onclick="loadAdvisorEvents()">Refresh</button>
          <button class="danger" onclick="clearAdvisorEvents()">Clear</button>
          <span class="subtle">Shows what Unity reports when ML Advisor or LLM Advisor buttons are clicked in the scene.</span>
        </div>
        <div id="advisor-feed" class="advisor-feed"><span class="advisor-empty">No advisor events yet.</span></div>
      </div>
    </section>

    <section class="tab-panel" data-tab="analysis">
      <div class="panel">
        <div class="row" style="align-items:center; gap:10px; flex-wrap:wrap">
          <h2 style="margin:0">Benchmark Data Analysis</h2>
          <label class="subtle" style="display:flex; align-items:center; gap:6px">
            Games:
            <select id="analysis-source" onchange="loadAnalysis()">
              <option value="all">all</option>
              <option value="benchmark">benchmark only</option>
              <option value="interactive">interactive only</option>
            </select>
          </label>
          <label class="subtle" style="display:flex; align-items:center; gap:6px" title="Filter by the player-type matchup (order-independent, e.g. Algorithm vs Human). Populated from the games available in the current source view.">
            Matchup:
            <select id="analysis-matchup" onchange="loadAnalysis()">
              <option value="all">All matchups</option>
            </select>
          </label>
          <button class="secondary" id="analysis-refresh-btn" onclick="loadAnalysis()">Refresh</button>
          <span id="analysis-status" class="subtle"></span>
        </div>
        <p class="subtle" style="margin:6px 0 0">
          Exploratory analysis of <span class="mono">games.jsonl</span>: descriptive statistics,
          distribution shape, correlation and hypothesis tests over benchmark results.
        </p>
      </div>
      <div id="analysis-body"><span class="subtle">Loading analysis…</span></div>
    </section>

    <section class="panel" id="activity">
      <div class="activity-head">
        <h2>Process Log</h2>
        <div class="row">
          <button class="secondary" onclick="loadLog('train', true)">Training</button>
          <button class="secondary" onclick="loadLog('eval', true)">Evaluation</button>
          <button class="secondary" onclick="loadLog('setup', true)">Setup</button>
          <button class="secondary" onclick="loadLog('sync', true)">Log sync</button>
          <button class="secondary" id="raw-btn" onclick="toggleRaw()">Show raw status</button>
          <button class="secondary" onclick="copyLog()">Copy</button>
          <button class="secondary" onclick="downloadLog()">Download</button>
          <button class="secondary" id="dock-log-btn" onclick="toggleDockLog()">Dock right</button>
        </div>
      </div>
      <pre id="log">No log loaded.</pre>
      <div id="log-eta" class="log-eta"></div>
      <pre id="raw" style="display:none">Loading...</pre>
    </section>
  </main>
  <script>
    let lastStatus = {};
    let wasSyncing = false;

    async function api(path, options = {}) {
      const res = await fetch(path, options);
      const data = await res.json();
      if (!res.ok || data.error) throw new Error(data.error || JSON.stringify(data));
      return data;
    }

    async function refresh() {
      const data = await api('/api/status');
      lastStatus = data;
      const keys = ['decision_files','decision_records','usable_decisions','invalid_decisions','cards_loaded','input_dim','loaded_model'];
      // loaded_model holds a long absolute .pt path — give it two columns and show just the filename (full path on hover).
      document.getElementById('cards').innerHTML = keys.map(k => {
        if (k === 'loaded_model') {
          const full = data[k];
          const name = full ? String(full).split(/[/\\]/).pop() : '-';
          return `<div class="card wide"><div class="label">${k}</div><div class="value" title="${escapeHtml(full ?? '')}">${escapeHtml(name)}</div></div>`;
        }
        return `<div class="card"><div class="label">${k}</div><div class="value">${data[k] ?? '-'}</div></div>`;
      }).join('');
      document.getElementById('raw').textContent = JSON.stringify(data, null, 2);
      updateGamesHint();
      const isTraining = data.training === 'running';
      if (!isTraining) { const etaEl = document.getElementById('log-eta'); if (etaEl) etaEl.textContent = ''; }
      document.getElementById('train-start').disabled = isTraining;
      toggleStop('train-stop', isTraining);
      // Fine-tune buttons mirror the training state.
      const ftStart = document.getElementById('ft-start');
      const ftStop = document.getElementById('ft-stop');
      if (ftStart) ftStart.disabled = isTraining;
      toggleStop('ft-stop', isTraining);
      // Curriculum: hide/show Run vs Stop; show current stage label.
      const cur = data.curriculum || {};
      const curRunning = isTraining && cur.active;
      const curStart = document.getElementById('cur-start');
      if (curStart) curStart.disabled = isTraining;
      toggleStop('cur-stop', curRunning);
      if (curRunning) {
        const panel = document.getElementById('cur-stop')?.closest('details');
        if (panel) panel.open = true;
      }
      const stageLabel = document.getElementById('cur-stage-label');
      if (stageLabel) {
        if (curRunning) {
          stageLabel.textContent = cur.stage === 1
            ? '● Stage 1/2 — pre-training (all decisions)'
            : `● Stage 2/2 — fine-tuning${cur.stage1_model ? ' from ' + cur.stage1_model.split(/[/\\]/).pop() : ''}`;
        } else {
          stageLabel.textContent = cur.stage1_model && !cur.active
            ? '✓ Complete — stage 1 model: ' + cur.stage1_model.split(/[/\\]/).pop()
            : '';
        }
      }
      toggleStop('eval-stop', data.evaluation === 'running');
      toggleStop('setup-stop', data.setup === 'running');
      // Log-sync: disable the fetch button while a sync runs; surface the last result.
      const syncing = data.log_sync === 'running';
      const fetchBtn = document.getElementById('fetch-logs-btn');
      if (fetchBtn) {
        fetchBtn.disabled = syncing;
        fetchBtn.textContent = syncing ? '⏳ Fetching logs…' : '⬇ Fetch logs from server';
      }
      const mirrorBtn = document.getElementById('mirror-decisions-btn');
      if (mirrorBtn) {
        mirrorBtn.disabled = syncing;
        mirrorBtn.textContent = syncing ? '⏳ Synchronizing…' : '↔ Sync ML logs mirror';
      }
      const note = document.getElementById('scan-note');
      if (!syncing && data.log_sync_summary && note && wasSyncing) {
        const s = data.log_sync_summary;
        note.textContent = s.ok
          ? `Log sync done: ${s.message || `copied ${s.copied}, skipped ${s.skipped} existing${s.failed ? `, failed ${s.failed}` : ''}`}.`
          : `Log sync failed: ${s.error || 'see Log sync tab'}.`;
      }
      wasSyncing = syncing;
      updateStages(data);
      renderDataset();
    }

    function toggleStop(id, running) {
      const el = document.getElementById(id);
      if (el) el.style.display = running ? '' : 'none';
    }

    function stage(id, status, note) {
      const el = document.getElementById(id);
      const noteEl = document.getElementById(id + '-note');
      if (!el || !noteEl) return;
      el.classList.toggle('running', status === 'running');
      el.classList.toggle('done', status === 'done');
      noteEl.textContent = note || status || 'stopped';
    }

    function updateStages(data) {
      stage('stage-setup', data.setup === 'running' ? 'running' : 'done', data.setup);
      stage('stage-dataset', data.usable_decisions ? 'done' : 'idle', data.usable_decisions ? `${data.usable_decisions} usable` : 'scan required');
      stage('stage-train', data.training === 'running' ? 'running' : (String(data.training).startsWith('exited(0)') ? 'done' : 'idle'), data.training);
      stage('stage-eval', data.evaluation === 'running' ? 'running' : (String(data.evaluation).startsWith('exited(0)') ? 'done' : 'idle'), data.evaluation);
      stage('stage-model', data.loaded_model ? 'done' : 'idle', data.loaded_model ? 'loaded' : 'not loaded');
      // stage-metrics is updated by loadMetrics()
    }

    function updateGamesHint() {
      const el = document.getElementById('train-games-avail');
      if (!el) return;
      const winnersOnly = (document.getElementById('train-winners-only') || {}).checked;
      // Winners-only can only use games that have a recorded winner in games.jsonl.
      const files = lastStatus.decision_files ?? 0;
      const metadataKnown = lastStatus.games_with_metadata != null;
      const avail = winnersOnly && metadataKnown ? lastStatus.games_with_metadata : files;
      const want = Number(document.getElementById('train-max').value) || 0;
      const used = want > 0 ? Math.min(want, avail) : avail;
      // Average decisions/game, if a scan has populated usable_decisions; else fall back.
      const avg = (lastStatus.usable_decisions && lastStatus.decision_files)
        ? lastStatus.usable_decisions / lastStatus.decision_files : 0;
      // Winners-only keeps roughly the winner's half of each game's decisions.
      const est = avg ? ` · ~${Math.round(used * avg * (winnersOnly ? 0.5 : 1)).toLocaleString()} decisions` : '';
      const scanNote = avg ? '' : ' · scan dataset to estimate decisions';
      const tag = winnersOnly
        ? (metadataKnown ? ' (with winner)' : ' (winner metadata not scanned)')
        : '';
      el.textContent = `${avail} games available${tag} · 0 = all${want > 0 ? ` · using ${used}` : ''}${est}${scanNote}`;
    }

    function saveTrainForm() {
      const ids = ['train-device','train-max','train-epochs','train-valratio','train-seed','train-log-every','train-patience','train-grad-accum','train-lr',
                   'ft-max','ft-epochs','ft-lr','ft-grad-accum'];
      const form = {};
      ids.forEach(id => { const el = document.getElementById(id); if (el) form[id] = el.value; });
      ['train-winners-only','train-turn-meta','ft-winners-only'].forEach(id => {
        const el = document.getElementById(id); if (el) form[id] = el.checked;
      });
      form['train-profile-cbs'] = Array.from(document.querySelectorAll('.train-profile-cb:checked')).map(cb => cb.value);
      try { localStorage.setItem('tcgml-train-form', JSON.stringify(form)); } catch (e) {}
    }

    function restoreTrainForm() {
      let form = {};
      try { form = JSON.parse(localStorage.getItem('tcgml-train-form') || '{}'); } catch (e) {}
      Object.entries(form).forEach(([id, val]) => {
        const el = document.getElementById(id);
        if (!el) return;
        if (el.type === 'checkbox') el.checked = !!val; else el.value = val;
      });
      const profileChecks = form['train-profile-cbs'];
      if (Array.isArray(profileChecks)) {
        document.querySelectorAll('.train-profile-cb').forEach(cb => { cb.checked = profileChecks.includes(cb.value); });
      }
      const ids = ['train-device','train-max','train-epochs','train-valratio','train-seed','train-log-every','train-patience','train-grad-accum','train-lr',
                   'train-winners-only','train-turn-meta','ft-max','ft-epochs','ft-lr','ft-grad-accum','ft-winners-only'];
      ids.forEach(id => { const el = document.getElementById(id); if (el) el.addEventListener('change', saveTrainForm); });
      document.querySelectorAll('.train-profile-cb').forEach(cb => cb.addEventListener('change', saveTrainForm));
    }

    // Derive training parameters from the scanned dataset. The model uses early stopping with
    // best-checkpoint saving, so `epochs` is a safe ceiling (patience trims it). Heuristics are
    // grounded in the real scan fields (games, usable decisions, per-category counts, winner
    // metadata coverage) plus the static deck universe from the Decks/ folder.
    function computeTrainSuggestion(s) {
      const games = s.decision_files || 0;
      const usable = s.usable_decisions;            // null until a scan has run
      const meta = s.games_with_metadata;           // games with a recorded winner
      const cats = s.categories || null;            // per-category usable counts
      const decks = s.decks_available || 0;
      const records = s.decision_records || 0;
      const invalid = s.invalid_decisions || 0;
      const warnings = [], notes = [];

      if (usable == null) {
        warnings.push({ level: 'error', text: 'Dataset not scanned yet — run "Scan dataset" first so suggestions can use category counts.' });
        return { params: null, warnings, notes };
      }

      // val_ratio: small datasets need a larger held-out fraction or val_loss is pure noise.
      let valRatio = games < 50 ? 0.2 : games < 300 ? 0.15 : games < 1000 ? 0.1 : 0.08;
      // epochs: ceiling, scaled inversely to data size (early stopping does the real cutoff).
      let epochs = usable < 5000 ? 15 : usable < 30000 ? 10 : usable < 100000 ? 8 : 6;
      // grad_accum: bigger effective batch for bigger data — smoother and faster.
      let gradAccum = usable < 5000 ? 1 : usable < 30000 ? 8 : usable < 100000 ? 16 : 32;
      // patience: a small/noisy val set needs more slack before stopping.
      const valGames = Math.max(1, Math.round(games * valRatio));
      const patience = valGames < 60 ? 6 : 4;
      // Winner-metadata coverage. games.jsonl can hold MORE rows than there are
      // decision logs (games logged without a decision file), so meta/games can
      // exceed 1 — clamp to 100% to keep the % sane.
      const coverage = (meta && games) ? Math.min(1, meta / games) : 0;
      const haveWinners = (meta >= 1500 && coverage >= 0.9);
      // Training is Stage 1: it should stay BROAD (all decisions). Specialization
      // toward winning play belongs to Stage 2 (Fine-tune). A single winners-only
      // run is strictly worse than two-stage (it throws away the losing-side signal
      // instead of using it as a base), so a suggestion never ticks it here.
      const winnersOnly = false;

      // max_games: 0 = all (best final model). On a very large set a full pass is
      // slow, so recommend a subset sized to ~60k decisions for a fast first run.
      const perGame = games ? usable / games : 0;
      let maxGames = 0;
      let maxGamesNote = `Max games 0 (use all ${games} games) — the dataset is small enough for a full run.`;
      if (usable > 100000 && perGame > 0) {
        maxGames = Math.max(200, Math.round(60000 / perGame));
        maxGamesNote = `Max games ${maxGames} (~60k decisions, ~${Math.round(perGame)} per game) for a FAST first run. Set Max games to 0 once you want the final full-data model (~${Math.round(usable / 1000)}k decisions, slow).`;
      }

      // ---- fine-tune / Stage 2 suggestion ----
      // Stage 2 specializes a pre-trained model on winning play: fewer epochs, a
      // low LR so the base weights are not overwritten, same effective batch.
      const ftEpochs = Math.max(3, Math.round(epochs / 2));
      const ftGradAccum = gradAccum;
      // Lower LR on smaller winner sets so a few noisy batches can't wreck the base.
      // Stage 2 trains on winners only, so size it by the WINNER subset, not total
      // usable: winners-only keeps roughly the winning side (~half) of the games that
      // actually carry winner metadata (coverage). Using total usable here would pick
      // too-high an LR whenever winner labels are sparse but the raw dataset is large.
      const winnerUsable = Math.round(usable * coverage * 0.5);
      const ftLr = winnerUsable < 5000 ? 0.000005 : 0.00001;
      const finetune = { epochs: ftEpochs, grad_accum: ftGradAccum, lr: ftLr, winners_only: true, max_games: 0 };

      const params = { epochs, grad_accum: gradAccum, val_ratio: valRatio, patience, winners_only: winnersOnly, max_games: maxGames, finetune };

      notes.push(`${games} games · ${usable.toLocaleString()} usable decisions → epochs ${epochs} (ceiling), grad_accum ${gradAccum}, val_ratio ${valRatio} (~${valGames} val games), patience ${patience}.`);
      notes.push(maxGamesNote);
      notes.push(haveWinners
        ? `Recommended path: Two-stage training. Stage 1 = this Training form on ALL decisions (Winners-only OFF), Stage 2 = Fine-tune on winners only (${meta} games, ${Math.round(coverage * 100)}% coverage). That beats a single winners-only run — it keeps the losing-side signal as a base, then specializes.`
        : `Single Training run on all decisions is the safe baseline. Winner metadata is thin (${meta || 0} games, ${Math.round(coverage * 100)}% coverage), so a winners-only Stage 2 would have little to learn from — run two-stage only once you have more winner-labeled games.`);
      notes.push(`Stage 2 / Fine-tune suggested: winners-only, epochs ${ftEpochs}, grad_accum ${ftGradAccum}, lr ${ftLr} (all games). Used for the Fine-tune panel and the two-stage Stage 2.`);

      // ---- data-quality warnings ----
      if (games < 50 || usable < 3000)
        warnings.push({ level: 'error', text: `Very small dataset (${games} games, ${usable.toLocaleString()} decisions): expect overfitting and noisy validation — not benchmark-grade.` });

      const TRAINABLE = ['PlayBasic', 'AttachEnergy', 'Attack', 'Retreat', 'Evolve'];
      if (cats) TRAINABLE.forEach(c => {
        const n = cats[c] || 0;
        // 0 examples is worse than "few": the model can't learn this action class at
        // all, so macro-accuracy takes a guaranteed hit. Flag it harder than a thin one.
        if (n === 0)
          warnings.push({ level: 'error', text: `Category ${c} has NO examples — the model cannot learn this action at all (macro-accuracy will be capped). Generate games that exercise ${c}.` });
        else if (n < 200)
          warnings.push({ level: 'warn', text: `Category ${c} has only ${n} examples — the model will be weak there (drags down macro-accuracy).` });
      });

      if (meta != null && games && coverage < 0.9)
        warnings.push({ level: 'warn', text: `Only ${Math.round(coverage * 100)}% of games have winner metadata — winners-only would silently drop the rest.` });

      if (records && invalid / records > 0.05)
        warnings.push({ level: 'warn', text: `${Math.round(invalid / records * 100)}% of records are invalid — check the logger (see Dataset Breakdown for reasons).` });

      if (decks >= 2) {
        const matchups = decks * (decks - 1) / 2;
        const perMatchup = games / matchups;
        if (perMatchup < 3)
          warnings.push({ level: 'info', text: `${decks} decks defined → ~${matchups} possible matchups, but only ${games} games (~${perMatchup.toFixed(1)}/matchup). Coverage is thin; the model sees few examples per matchup. (Logging the deck per game in games.jsonl would pinpoint exact gaps.)` });
      }

      if (usable > 100000)
        warnings.push({ level: 'info', text: `Large dataset (~${Math.round(usable / 1000)}k decisions): a full run is slow, so Max games was set to ${maxGames} for a fast first pass. Set it to 0 once you want the final full-data model.` });

      return { params, warnings, notes };
    }

    function suggestTrainParams() {
      const { params, warnings, notes } = computeTrainSuggestion(lastStatus);
      if (params) {
        document.getElementById('train-max').value = params.max_games;
        document.getElementById('train-epochs').value = params.epochs;
        document.getElementById('train-grad-accum').value = params.grad_accum;
        document.getElementById('train-valratio').value = params.val_ratio;
        document.getElementById('train-patience').value = params.patience;
        document.getElementById('train-winners-only').checked = params.winners_only;
        // Stage 2 / Fine-tune form (also used as Stage 2 of two-stage training).
        if (params.finetune) {
          const ft = params.finetune;
          const ftEpochsEl = document.getElementById('ft-epochs');
          const ftGradEl = document.getElementById('ft-grad-accum');
          const ftLrEl = document.getElementById('ft-lr');
          const ftWoEl = document.getElementById('ft-winners-only');
          const ftMaxEl = document.getElementById('ft-max');
          if (ftEpochsEl) ftEpochsEl.value = ft.epochs;
          if (ftGradEl) ftGradEl.value = ft.grad_accum;
          if (ftLrEl) ftLrEl.value = ft.lr;
          if (ftWoEl) ftWoEl.checked = ft.winners_only;
          if (ftMaxEl) ftMaxEl.value = ft.max_games;
        }
        saveTrainForm();
        updateGamesHint();
      }
      const box = document.getElementById('train-suggest');
      if (!box) return;
      const icon = { error: '🔴', warn: '🟠', info: '🔵', ok: '🟢' };
      let html = '';
      if (params) html += `<div class="suggest-applied">✓ Applied (Training): max_games ${params.max_games || 'all'} · epochs ${params.epochs} · grad_accum ${params.grad_accum} · val_ratio ${params.val_ratio} · patience ${params.patience} · winners_only ${params.winners_only ? 'on' : 'off'}</div>`;
      if (params && params.finetune) html += `<div class="suggest-applied">✓ Applied (Fine-tune / Stage 2): epochs ${params.finetune.epochs} · grad_accum ${params.finetune.grad_accum} · lr ${params.finetune.lr} · winners_only on</div>`;
      notes.forEach(n => { html += `<div class="suggest-note">${escapeHtml(n)}</div>`; });
      warnings.forEach(w => { html += `<div class="suggest-warn ${w.level}">${icon[w.level] || ''} ${escapeHtml(w.text)}</div>`; });
      if (params && !warnings.length) html += `<div class="suggest-warn ok">🟢 No data-quality issues detected.</div>`;
      box.innerHTML = html;
      box.style.display = 'block';
    }

    // Decision-log sources (Decisions/ subfolders) the user ticked for training. Empty selection =>
    // omit the key so the backend applies its default (every non-legacy source).
    function getSelectedSources() {
      const boxes = document.querySelectorAll('#train-sources input[type=checkbox]:checked');
      return Array.from(boxes).map(b => b.value);
    }

    function buildStage1Payload() {
      return {
        device: document.getElementById('train-device').value,
        max_games: Number(document.getElementById('train-max').value),
        epochs: Number(document.getElementById('train-epochs').value),
        val_ratio: Number(document.getElementById('train-valratio').value),
        seed: Number(document.getElementById('train-seed').value),
        log_every: Number(document.getElementById('train-log-every').value),
        patience: Number(document.getElementById('train-patience').value),
        winners_only: document.getElementById('train-winners-only').checked,
        profiles: Array.from(document.querySelectorAll('.train-profile-cb:checked')).map(cb => cb.value),
        grad_accum: Number(document.getElementById('train-grad-accum')?.value || 1),
        lr: Number(document.getElementById('train-lr')?.value || 0.0001),
        sources: getSelectedSources(),
      };
    }

    async function startTraining() {
      const payload = buildStage1Payload();
      saveTrainForm();
      document.getElementById('log').textContent = JSON.stringify(await api('/api/train/start', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload) }), null, 2);
      loadLog('train', true);  // auto-switch the Process Log to the run that just started
      refresh();
    }

    async function loadTrainSources() {
      const host = document.getElementById('train-sources');
      if (!host) return;
      // Preserve the user's current ticks across refreshes (don't reset on every poll).
      const prev = {};
      host.querySelectorAll('input[type=checkbox]').forEach(b => { prev[b.value] = b.checked; });
      let data;
      try { data = await api('/api/dataset/sources'); }
      catch (e) { host.innerHTML = '<span class="muted">sources unavailable</span>'; return; }
      const sources = (data && data.sources) || [];
      if (!sources.length) {
        host.innerHTML = '<span class="muted">No decision logs yet. New games write to Decisions/benchmark or Decisions/interactive.</span>';
        return;
      }
      host.innerHTML = sources.map(s => {
        // Default: tick the new per-context sources; leave legacy (Decisions/ root) off.
        const checked = (s.name in prev) ? prev[s.name] : !s.legacy;
        return `<label class="check"><input type="checkbox" value="${escapeHtml(s.name)}"${checked ? ' checked' : ''}> ${escapeHtml(s.name)} <span class="muted">(${s.files})</span></label>`;
      }).join('');
    }

    async function refreshFtModelList() {
      const sel = document.getElementById('ft-from');
      if (!sel) return;
      try {
        const data = await api('/api/models');
        const models = (data.models || []);
        const current = sel.value;
        sel.innerHTML = '<option value="latest">latest</option>';
        models.forEach(m => {
          const name = m.path.split(/[/\\]/).pop();
          const meta = m.meta || {};
          const tags = [];
          tags.push(meta.from_model ? 'fine-tune' : 'training');
          if (meta.winners_only) tags.push('winners');
          if (meta.profile) tags.push(meta.profile);
          if (meta.val_acc != null) tags.push(`val ${(meta.val_acc * 100).toFixed(1)}%`);
          if (meta.val_macro_acc != null) tags.push(`macro ${(meta.val_macro_acc * 100).toFixed(1)}%`);
          if (meta.best_epoch || meta.completed_epochs || meta.epochs_requested) {
            tags.push(`ep ${meta.best_epoch || '?'}*/${meta.completed_epochs ?? '?'}/${meta.epochs_requested ?? '?'}`);
          }
          if (meta.from_model) tags.push(`from ${String(meta.from_model).split(/[/\\]/).pop()}`);
          const opt = document.createElement('option');
          opt.value = m.path;
          opt.textContent = `${name} - ${tags.join(' | ')}`;
          if (m.path === current) opt.selected = true;
          sel.appendChild(opt);
        });
      } catch (e) { /* silently ignore */ }
    }

    async function startFinetune() {
      const existingRuns = await currentMetricRuns();
      const baseRun = await baselineRunForFineTune(document.getElementById('ft-from').value, existingRuns);
      const payload = {
        device: document.getElementById('train-device').value,
        max_games: Number(document.getElementById('ft-max').value),
        epochs: Number(document.getElementById('ft-epochs').value),
        val_ratio: Number(document.getElementById('train-valratio').value),
        seed: Number(document.getElementById('train-seed').value),
        log_every: Number(document.getElementById('train-log-every').value),
        patience: Number(document.getElementById('train-patience').value),
        winners_only: document.getElementById('ft-winners-only').checked,
        from_model: document.getElementById('ft-from').value,
        lr: Number(document.getElementById('ft-lr').value),
        grad_accum: Number(document.getElementById('ft-grad-accum')?.value || 1),
        sources: getSelectedSources(),
      };
      saveTrainForm();
      document.getElementById('log').textContent = JSON.stringify(await api('/api/train/start', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload) }), null, 2);
      if (baseRun) pendingFineTuneCompare = { baseRun, knownRuns: new Set(existingRuns.map(r => r.name)) };
      loadLog('train', true);
      refresh();
    }

    async function startCurriculum() {
      const s1 = buildStage1Payload();
      // Stage 2 uses the Fine-tune form; the backend injects the Stage 1 checkpoint.
      s1.s2_max_games = Number(document.getElementById('ft-max').value);
      s1.s2_epochs = Number(document.getElementById('ft-epochs').value);
      s1.s2_lr = Number(document.getElementById('ft-lr').value);
      s1.s2_grad_accum = Number(document.getElementById('ft-grad-accum')?.value || 1);
      s1.s2_winners_only = document.getElementById('ft-winners-only').checked;
      // Stage 1 of curriculum always uses all decisions (override winners_only).
      s1.winners_only = false;
      saveTrainForm();
      document.getElementById('log').textContent = JSON.stringify(await api('/api/train/curriculum/start', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(s1) }), null, 2);
      loadLog('train', true);
      refresh();
    }

    async function stopCurriculum() {
      document.getElementById('log').textContent = JSON.stringify(await api('/api/train/curriculum/stop', { method: 'POST' }), null, 2);
      refresh();
    }

    async function checkEnvironment() {
      document.getElementById('log').textContent = JSON.stringify(await api('/api/environment'), null, 2);
      refresh();
    }

    async function loadPaths() {
      try {
        const d = await api('/api/paths');
        const c = d.current || {};
        const overridden = Object.keys(d.overrides || {}).length > 0;
        const cur = document.getElementById('paths-current');
        if (cur) {
          cur.innerHTML = 'Active ML logs: <b>' + escapeHtml(c.logs_ml_dir || '-') + '</b> — '
            + (c.decisions_exists ? (c.decision_files + ' decision files') : 'Decisions/ not found')
            + (c.games_jsonl_exists ? '' : ', no games.jsonl')
            + (overridden ? ' <span class="subtle">(custom override active)</span>' : ' <span class="subtle">(default)</span>');
        }
        const li = document.getElementById('path-logs');
        if (li && !li.dataset.dirty) li.value = (d.overrides && d.overrides.logs_dir) || '';
        const cd = document.getElementById('path-cards');
        if (cd && document.activeElement !== cd) cd.value = (d.overrides && d.overrides.cards_dir) || '';
        const dd = document.getElementById('path-decks');
        if (dd && document.activeElement !== dd) dd.value = (d.overrides && d.overrides.decks_dir) || '';
        const envDefaults = String(d.mirror_default || '').replace(/;/g, '\n').split('\n').map(s => s.trim()).filter(Boolean);
        const mp = document.getElementById('mirror-path');
        if (mp && document.activeElement !== mp && !mp.value) {
          let saved = '';
          try { saved = localStorage.getItem('tcgml-mirror-path') || ''; } catch (e) {}
          mp.value = saved || envDefaults[0] || '';
        }
        const mp2 = document.getElementById('mirror-path-2');
        if (mp2 && document.activeElement !== mp2 && !mp2.value) {
          let saved2 = '';
          try { saved2 = localStorage.getItem('tcgml-mirror-path-2') || ''; } catch (e) {}
          mp2.value = saved2 || envDefaults[1] || '';
        }
        const dirSel = document.getElementById('mirror-direction');
        if (dirSel && document.activeElement !== dirSel) {
          let savedDir = '';
          try { savedDir = localStorage.getItem('tcgml-mirror-direction') || ''; } catch (e) {}
          if (savedDir) dirSel.value = savedDir;
        }
      } catch (e) {}
    }

    async function checkPaths() {
      const note = document.getElementById('paths-note');
      const v = (document.getElementById('path-logs').value || '').trim();
      if (!v) { if (note) note.textContent = 'Enter an ML logs path first.'; return; }
      try {
        const r = await api('/api/paths/check', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ logs_dir: v }) });
        if (note) note.textContent = r.exists
          ? ('OK → ' + r.resolved_logs_ml_dir + ' — ' + (r.decisions_exists ? (r.decision_files + ' decision files') : 'no Decisions/ yet') + (r.games_jsonl_exists ? ', games.jsonl present' : ', no games.jsonl') + '.')
          : ('Not found: ' + r.resolved_logs_ml_dir);
      } catch (e) { if (note) note.textContent = 'Check failed: ' + e.message; }
    }

    async function applyPaths() {
      const note = document.getElementById('paths-note');
      const payload = {
        logs_dir: (document.getElementById('path-logs').value || '').trim(),
        cards_dir: (document.getElementById('path-cards').value || '').trim(),
        decks_dir: (document.getElementById('path-decks').value || '').trim(),
      };
      if (!payload.logs_dir && !payload.cards_dir && !payload.decks_dir) { if (note) note.textContent = 'Nothing to apply.'; return; }
      try {
        const r = await api('/api/paths', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload) });
        if (note) note.textContent = r.applied
          ? ('Applied. Active ML logs: ' + r.logs_ml_dir + ' (' + r.decision_files + ' decision files).')
          : (r.error || 'Nothing applied.');
        const li = document.getElementById('path-logs'); if (li) delete li.dataset.dirty;
        await loadPaths();
        await refresh();
        if (r.applied) scanDataset().catch(() => {});
      } catch (e) { if (note) note.textContent = 'Apply failed: ' + e.message; }
    }

    async function resetPaths() {
      const note = document.getElementById('paths-note');
      try {
        const r = await api('/api/paths', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ reset: true }) });
        ['path-logs','path-cards','path-decks'].forEach(id => { const el = document.getElementById(id); if (el) { el.value = ''; delete el.dataset.dirty; } });
        if (note) note.textContent = 'Reset to default. Active ML logs: ' + r.logs_ml_dir + '.';
        await loadPaths();
        await refresh();
      } catch (e) { if (note) note.textContent = 'Reset failed: ' + e.message; }
    }

    async function scanDataset() {
      const bar = document.getElementById('scan-progress');
      const note = document.getElementById('scan-note');
      const btn = document.getElementById('scan-btn');
      bar.classList.add('active');
      note.textContent = 'Scanning dataset...';
      btn.disabled = true;
      try {
        const data = await api('/api/dataset/scan', { method: 'POST' });
        note.textContent = `Done: ${data.usable_decisions ?? 0} usable / ${data.decision_records ?? 0} records in ${data.decision_files ?? 0} file(s).`;
        refresh();
      } catch (e) {
        note.textContent = 'Scan failed: ' + e.message;
      } finally {
        bar.classList.remove('active');
        btn.disabled = false;
      }
    }

    async function fetchServerLogs() {
      const note = document.getElementById('scan-note');
      try {
        const res = await api('/api/logs/sync', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ mode: 'server-fetch' }) });
        if (res.started === false) {
          if (note) note.textContent = 'Log sync already running.';
        } else if (note) {
          note.textContent = 'Fetching logs from server… (see the Log sync tab below)';
        }
        loadLog('sync', true);  // switch the Process Log to the sync stream
        refresh();
      } catch (e) {
        if (note) note.textContent = 'Fetch failed: ' + e.message;
      }
    }

    async function syncDecisionLogsMirror() {
      const note = document.getElementById('scan-note');
      const input1 = document.getElementById('mirror-path');
      const input2 = document.getElementById('mirror-path-2');
      const dir1 = ((input1 && input1.value) || '').trim();
      const dir2 = ((input2 && input2.value) || '').trim();
      const dirSel = document.getElementById('mirror-direction');
      const direction = (dirSel && dirSel.value) || 'two-way';
      // Two-way needs both devices; Pull/Push can mirror a single chosen device.
      if (direction === 'two-way' && (!dir1 || !dir2)) {
        if (note) note.textContent = 'Two-way sync needs both device paths. To mirror just one device, pick Pull or Push.';
        if (!dir1 && input1) input1.focus(); else if (input2) input2.focus();
        return;
      }
      const mirrorDirs = [dir1, dir2].filter(Boolean);
      if (mirrorDirs.length === 0) {
        if (note) note.textContent = 'Enter at least one device logs-copy path (build root, Logs Export, …/ML, or the Decisions folder).';
        if (input1) input1.focus();
        return;
      }
      try { localStorage.setItem('tcgml-mirror-path', dir1); localStorage.setItem('tcgml-mirror-path-2', dir2); localStorage.setItem('tcgml-mirror-direction', direction); } catch (e) {}
      try {
        const res = await api('/api/logs/sync', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ mode: 'decisions-mirror', mirror_dirs: mirrorDirs, direction }) });
        if (res.started === false) {
          if (note) note.textContent = 'Log sync already running.';
        } else if (note) {
          note.textContent = 'Synchronizing ML logs mirror… (see the Log sync tab below)';
        }
        loadLog('sync', true);
        refresh();
      } catch (e) {
        if (note) note.textContent = 'Mirror sync failed: ' + e.message;
      }
    }

    let jsonPatchBackupResolver = null;
    function askJsonPatchBackupLogs() {
      const modal = document.getElementById('json-patch-backup-modal');
      if (!modal) return Promise.resolve(false);
      modal.classList.add('open');
      return new Promise(resolve => { jsonPatchBackupResolver = resolve; });
    }
    function resolveJsonPatchBackupChoice(choice) {
      const modal = document.getElementById('json-patch-backup-modal');
      if (modal) modal.classList.remove('open');
      if (jsonPatchBackupResolver) {
        jsonPatchBackupResolver(Boolean(choice));
        jsonPatchBackupResolver = null;
      }
    }

    async function downloadJsonPatch() {
      const note = document.getElementById('scan-note');
      const btn = document.getElementById('download-patch-btn');
      const backupLogs = await askJsonPatchBackupLogs();
      if (btn) btn.disabled = true;
      try {
        const res = await api('/api/logs/sync', {
          method: 'POST',
          headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({ mode: 'json-patch', backup_logs: backupLogs })
        });
        if (res.started === false) {
          if (note) note.textContent = 'Synchronize operation already running.';
        } else if (note) {
          note.textContent = 'Downloading card/deck JSON patch… (see the Log sync tab below)';
        }
        loadLog('sync', true);
        refresh();
      } catch (e) {
        if (note) note.textContent = 'Download patch failed: ' + e.message;
      } finally {
        if (btn) btn.disabled = false;
      }
    }

    function toggleRaw() {
      const raw = document.getElementById('raw');
      const btn = document.getElementById('raw-btn');
      const show = raw.style.display === 'none';
      raw.style.display = show ? '' : 'none';
      btn.textContent = show ? 'Hide raw status' : 'Show raw status';
    }

    function applyDockLog(docked) {
      const panel = document.getElementById('activity');
      const btn = document.getElementById('dock-log-btn');
      if (!panel) return;
      panel.classList.toggle('docked', docked);
      document.body.classList.toggle('has-docked-log', docked);
      if (btn) btn.classList.toggle('active', docked);
      if (btn) btn.textContent = docked ? 'Undock' : 'Dock right';
      if (docked) { const log = document.getElementById('log'); if (log) log.scrollTop = log.scrollHeight; }
    }

    function toggleDockLog() {
      const docked = !document.getElementById('activity').classList.contains('docked');
      try { localStorage.setItem('tcgml-dock-log', docked ? '1' : '0'); } catch (e) {}
      applyDockLog(docked);
    }

    async function startSetup() {
      const payload = { profile: document.getElementById('setup-profile').value };
      document.getElementById('log').textContent = JSON.stringify(await api('/api/setup/start', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload) }), null, 2);
      refresh();
    }

    async function stopSetup() {
      document.getElementById('log').textContent = JSON.stringify(await api('/api/setup/stop', { method: 'POST' }), null, 2);
      refresh();
    }

    async function stopTraining() {
      document.getElementById('log').textContent = JSON.stringify(await api('/api/train/stop', { method: 'POST' }), null, 2);
      refresh();
    }

    async function startEvaluation() {
      const payload = {
        device: document.getElementById('train-device').value,
        log_every: Number(document.getElementById('train-log-every').value)
      };
      document.getElementById('log').textContent = JSON.stringify(await api('/api/evaluate/start', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(payload) }), null, 2);
      refresh();
    }

    async function stopEvaluation() {
      document.getElementById('log').textContent = JSON.stringify(await api('/api/evaluate/stop', { method: 'POST' }), null, 2);
      refresh();
    }

    async function loadLatest() {
      document.getElementById('log').textContent = JSON.stringify(await api('/api/load-model', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: '{}' }), null, 2);
      refresh();
      loadModels();
    }

    function fmtBytes(n) {
      if (!n && n !== 0) return '-';
      const u = ['B', 'KB', 'MB', 'GB']; let i = 0; let v = n;
      while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
      return v.toFixed(v < 10 && i > 0 ? 1 : 0) + ' ' + u[i];
    }

    async function loadModels() {
      const el = document.getElementById('models-table');
      let data;
      try { data = await api('/api/models'); } catch (e) { el.innerHTML = '<span class="subtle">Failed to list models.</span>'; return; }
      const models = data.models || [];
      if (!models.length) { el.innerHTML = '<span class="subtle">No .pt models found in models/.</span>'; return; }
      const loaded = lastStatus.loaded_model || '';
      const rows = models.map(m => {
        const name = m.path.split(/[\\/]/).pop();
        const when = new Date(m.trained_unix_ms || m.modified_unix_ms).toLocaleString();
        const isLoaded = m.path === loaded;
        const esc = m.path.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
        const escName = name.replace(/'/g, "\\'");
        const meta = m.meta;
        let train = '<span class="subtle">no metadata</span>';
        if (meta) {
          const acc = meta.val_acc != null ? `val_acc ${(meta.val_acc * 100).toFixed(1)}%` : 'val_acc —';
          const macro = meta.val_macro_acc != null ? ` · macro ${(meta.val_macro_acc * 100).toFixed(1)}%` : '';
          const ep = meta.best_epoch ? `${meta.best_epoch}*/${meta.completed_epochs ?? 0}/${meta.epochs_requested ?? '?'}` : `${meta.completed_epochs ?? 0}/${meta.epochs_requested ?? '?'}`;
          const isFineTune = !!meta.from_model;
          const stage = isFineTune ? '<span class="tag finetune">fine-tune</span> ' : '<span class="tag train">training</span> ';
          const wo = meta.winners_only ? '<span class="tag">winners only</span> ' : '';
          const pf = meta.profile ? `<span class="tag">${meta.profile}</span> ` : '';
          const tm = meta.include_turn_meta ? '<span class="tag">+TurnMeta</span> ' : '';
          // patch_no auto-increments on every Download patch (0 = pre-tracking models).
          // patch_ts format: YYYYMMDD_HHMMSS (set by Download patch); rendered as the date.
          const patchTs = meta.patch_ts ? String(meta.patch_ts) : '';
          const patchDate = patchTs.length >= 8
            ? `${patchTs.slice(0,4)}-${patchTs.slice(4,6)}-${patchTs.slice(6,8)}`
            : '';
          let patch;
          if (typeof meta.patch_no === 'number') {
            const label = patchDate ? `patch ${meta.patch_no} (${patchDate})` : `patch ${meta.patch_no}`;
            patch = `<span class="tag" title="Card/deck patch this model was trained under (patch #${meta.patch_no}${patchDate ? `, downloaded ${patchDate}` : ''})">${label}</span> `;
          } else if (patchTs.length >= 8) {
            // Legacy model: a patch timestamp but no number assigned.
            patch = `<span class="tag" title="Card/deck patch active when this model was trained (patch ${escapeHtml(patchTs)})">patch ${patchDate}</span> `;
          } else if (patchTs) {
            // Legacy "patch 0" and other manually assigned labels (predate patch_no tracking).
            patch = `<span class="tag" title="Manually assigned patch label (predates patch tracking)">patch ${escapeHtml(patchTs)}</span> `;
          } else {
            patch = '<span class="tag" title="Trained before patch tracking or without a dashboard-applied patch">patch ?</span> ';
          }
          const baseName = isFineTune ? String(meta.from_model).split(/[\\/]/).pop() : '';
          const base = isFineTune
            ? ` · from <span class="mono">${escapeHtml(baseName)}</span>${m.base_model_exists === false ? ' <span class="tag missing">missing base</span>' : ''}`
            : '';
          train = `<span class="subtle">${stage}${wo}${pf}${tm}${patch}${meta.train_games ?? '?'}g train · ${meta.val_games ?? '?'}g val · `
                + `${(meta.train_examples ?? 0).toLocaleString()} dec · ${ep} ep · `
                + `seed ${meta.seed ?? '?'} · <strong>${acc}${macro}</strong>${base}</span>`;
        }
        return `<tr class="${isLoaded ? 'loaded-row' : ''}">
          <td><span class="mono">${name}</span>${isLoaded ? ' <strong>(loaded)</strong>' : ''}<br>${train}</td>
          <td>${fmtBytes(m.bytes)}</td>
          <td>${when}</td>
          <td><button onclick="loadModelPath('${esc}')">Load</button>
              <button class="danger" onclick="deleteModelPath('${esc}', '${escName}')">Delete</button></td>
        </tr>`;
      }).join('');
      el.innerHTML = `<table><thead><tr><th>Model</th><th>Size</th><th>Trained</th><th></th></tr></thead><tbody>${rows}</tbody></table>`;
    }

    async function loadModelPath(path) {
      try {
        await api('/api/load-model', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ path }) });
      } catch (e) { alert('Load failed: ' + e.message); return; }
      refresh();
      loadModels();
    }

    async function deleteModelPath(path, name) {
      if (!confirm('Delete model "' + (name || path) + '"? This permanently removes the .pt file.')) return;
      try {
        await api('/api/delete-model', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ path }) });
      } catch (e) { alert('Delete failed: ' + e.message); return; }
      metricsSig = null;
      await refresh();
      await loadModels();
      await loadRunPicker();
      await loadMetrics();
    }

    async function loadLog(kind, force = false) {
      const el = document.getElementById('log');
      // Stick to newest only if the user is already near the bottom (or explicitly
      // clicked a log button). Lets them scroll up to read without being yanked down.
      const nearBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 40;
      const data = await api(`/api/logs/${kind}`);
      el.textContent = data.text || 'No log yet.';
      if (force || nearBottom) el.scrollTop = el.scrollHeight;
      // ETA only makes sense for an in-progress training log; clear it for any other log.
      updateTrainEta(kind === 'train' ? (data.text || '') : null);
    }

    function fmtDuration(sec) {
      sec = Math.max(0, Math.round(sec));
      const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
      if (h) return `${h}h ${m}m`;
      if (m) return `${m}m ${s}s`;
      return `${s}s`;
    }

    // Estimate remaining training time from the latest `phase=train step=S/N ... elapsed_s=EL`
    // line: per-step time = EL/S, remaining = (rest of this epoch) + (whole future epochs).
    // Rough but good enough — refreshed every poll while a run is active.
    function updateTrainEta(text) {
      const etaEl = document.getElementById('log-eta');
      if (!etaEl) return;
      if (text == null || lastStatus.training !== 'running') { etaEl.textContent = ''; return; }
      const re = /epoch=(\d+)\/(\d+)\s+phase=train\s+step=(\d+)\/(\d+)\s+loss=[\d.]+\s+acc=[\d.]+\s+elapsed_s=([\d.]+)/g;
      let m, last = null;
      while ((m = re.exec(text)) !== null) last = m;
      if (!last) { etaEl.textContent = '⏳ Training… estimating time remaining'; return; }
      const epoch = +last[1], epochsTotal = +last[2], step = +last[3], steps = +last[4], elapsed = +last[5];
      if (step <= 0) { etaEl.textContent = '⏳ Training…'; return; }
      const perStep = elapsed / step;
      const remainSteps = Math.max(0, steps - step) + Math.max(0, epochsTotal - epoch) * steps;
      etaEl.textContent = `⏳ ~${fmtDuration(remainSteps * perStep)} left · epoch ${epoch}/${epochsTotal} · step ${step.toLocaleString()}/${steps.toLocaleString()}`;
    }

    function currentLogText() {
      const raw = document.getElementById('raw');
      const showingRaw = raw && raw.style.display !== 'none';
      return (showingRaw ? raw : document.getElementById('log')).textContent || '';
    }

    async function copyLog() {
      try { await navigator.clipboard.writeText(currentLogText()); }
      catch (e) { alert('Copy failed: ' + e.message); }
    }

    function downloadLog() {
      const blob = new Blob([currentLogText()], { type: 'text/plain' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = 'process_log.txt';
      a.click();
      URL.revokeObjectURL(a.href);
    }

    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    const EXPERT_REASON_HELP = [
      ['BLOCK not a Basic Pokemon', 'PlayBasic candidate is not a stage-0 Pokemon, so AlgorithmBrain rejects it.'],
      ['BLOCK bench is full', 'There is no legal bench slot for this Basic while an active Pokemon already exists.'],
      ['BLOCK last bench slot reserved for typed EnergyRamp engine', 'The last bench slot is kept for a Basic that can ramp the specific scarce energy type needed by a strategic attacker.'],
      ['BLOCK bench slot conserved; duplicate Basic already on board', 'A duplicate low-value Basic would consume scarce bench space without adding a useful new line.'],
      ['BLOCK last bench slot reserved for strongest evolution line', 'The final bench slot is saved for the strongest available evolution line.'],
      ['Basic can improve board', 'Baseline positive value for playing a legal Basic Pokemon.'],
      ['fills empty active slot', 'Strong bonus when the board has no active Pokemon and this Basic fills it.'],
      ['EnergyRamp engine', 'The Basic has an attack that can attach or move extra energy and can become a setup engine.'],
      ['<type> EnergyRamp needed for strategic attacker', 'A typed ramp engine matches the energy type needed by a high-value future attacker.'],
      ['strongest evolution line', 'This Basic belongs to the strongest evolution line currently identified for the deck state.'],
      ['future scaling damage line', 'This line can later scale damage from stored energy or a similar payoff.'],
      ['has future evolution', 'This Basic has an evolution path, so it can improve later instead of staying a low-ceiling body.'],
      ['printed attack ceiling', 'Small value from the card\'s maximum printed damage, capped by profile settings.'],
      ['weak use of last bench slot', 'Penalty for spending the final bench slot on a Basic without ramp, strongest-line, or scaling value.'],
      ['BLOCK energy zone is empty', 'AttachEnergy is impossible because no energy is currently available.'],
      ['BLOCK invalid target', 'AttachEnergy target is missing Pokemon state or PokemonData.'],
      ['energy type advances an attack cost', 'The current energy type directly helps pay at least one attack on this Pokemon.'],
      ['energy advances a future evolution\'s attack cost', 'The energy does not help now, but it helps an attack on a known future evolution.'],
      ['energy type does not advance a normal attack', 'Penalty when the available energy type does not pay this Pokemon\'s current or future normal attack costs.'],
      ['energy reserved for strategic discard attacker', 'This Pokemon has a high-value discard-energy attack plan, so extra energy may be reserved for it.'],
      ['backup discard attacker needed after likely KO', 'The active discard attacker is likely to be KO\'d, so a backup attacker needs energy.'],
      ['active attacks this turn after attach', 'Attaching makes the active Pokemon ready to attack immediately.'],
      ['bench becomes ready', 'Attaching makes a benched Pokemon ready to attack after a future switch or retreat.'],
      ['reduces attack energy deficit', 'Attaching lowers the number of missing energy needed for an attack.'],
      ['unlocks or prepares a more expensive attack', 'The Pokemon was already ready for something, and this energy moves it toward a stronger attack.'],
      ['active can use energy immediately', 'Active targets get extra value because the energy can affect this turn.'],
      ['attach lets active KO the enemy active this turn', 'Attaching newly enables a lethal attack against the opponent\'s active Pokemon.'],
      ['safe reserve for next high-damage discard attack', 'Extra energy can be stockpiled for a discard attack and the active is not expected to die first.'],
      ['discard reserve is unsafe because active likely KO', 'Penalty for stockpiling discard-attack energy on an active that is likely to be KO\'d.'],
      ['active likely KO before unfinished energy matters', 'Penalty for investing unfinished energy into an active that probably dies before using it.'],
      ['bad type on active that is likely KO', 'Penalty for putting a non-helpful energy type on a threatened active Pokemon.'],
      ['builds bench threat safely', 'Benched targets get value because they can grow outside immediate active danger.'],
      ['bench stockpiles reserve for discard attack', 'A benched discard attacker can safely hold extra energy for a future high-damage attack.'],
      ['preserves energy away from threatened active', 'When the active is likely to die, bench energy is safer.'],
      ['EnergyRamp engine target', 'The attach target has an EnergyRamp attack and can become or improve the ramp engine.'],
      ['ramp amount', 'Extra value proportional to how much energy the card can ramp.'],
      ['do not consume finisher energy on ramp engine', 'Penalty when the same energy should be saved for another strategic finisher instead of the ramp engine.'],
      ['ramp attack becomes ready', 'Attaching makes the ramp attack usable.'],
      ['active ramp starts chain sooner', 'The active ramp target can begin the energy chain immediately.'],
      ['active ramp can fuel strategic bench attacker', 'Charging the active ramp engine can accelerate a strategic benched attacker.'],
      ['moves scaling/Psychic plan toward biggest enemy HP + reserve', 'Energy advances a scaling-damage plan sized against the opponent\'s largest HP target plus reserve.'],
      ['scaling/Psychic reserve is complete', 'The scaling plan has enough reserve energy after this attach.'],
      ['extra energy supports discard-attack plan', 'Extra energy helps a Pokemon whose attack discards energy.'],
      ['attacker ceiling', 'Small value from this target\'s maximum printed attack damage, capped by profile settings.'],
      ['no concrete payoff after attach', 'Penalty when the attach does not help current attacks, future attacks, scaling, or immediate readiness.'],
      ['BLOCK invalid active or target', 'Retreat scoring cannot evaluate because active or bench target state is missing.'],
      ['BLOCK manual retreat already used', 'Manual retreat has already been spent this turn.'],
      ['BLOCK active cannot retreat', 'The active Pokemon is prevented from retreating by state or status.'],
      ['BLOCK active has no PokemonData', 'The active card is missing PokemonData required to compute retreat cost and attacks.'],
      ['BLOCK not enough energy to retreat (x/y)', 'The active Pokemon has fewer attached energy than the current retreat cost.'],
      ['BLOCK target is not ready to attack', 'The bench target cannot attack after becoming active, so retreat is rejected.'],
      ['BLOCK bench target should wait for final evolution unless it wins now', 'A bench Pokemon with final evolution in hand should not be promoted unless it immediately wins.'],
      ['BLOCK active can KO current active this turn — attack instead of retreating', 'The active can already KO the opponent, so retreat would waste the attack and retreat energy.'],
      ['active likely KO next turn', 'Retreat gets value from saving an active Pokemon that is expected to be KO\'d.'],
      ['retreat target likely KO next turn', 'Penalty when the promoted bench target is likely to be KO\'d and does not win now.'],
      ['retreat target survival buffer', 'Value from the target having HP above the opponent\'s expected damage.'],
      ['active has no damage and no useful ramp', 'Retreat gets value when the current active cannot deal damage or provide useful ramp.'],
      ['active has useful ramp attack', 'Penalty for retreating away from an active that can perform useful ramp.'],
      ['retreat spends active energy', 'Penalty proportional to energy spent on retreat.'],
      ['target attack switches after manual retreat', 'Penalty when the target\'s best attack swaps itself out after a manual retreat.'],
      ['damage delta vs active', 'Difference between the bench target\'s ready damage and the active Pokemon\'s ready damage.'],
      ['bench can KO current active', 'Retreat target can KO the opponent\'s active while current active cannot.'],
      ['bench is much stronger attacker', 'Retreat target has substantially higher ready damage than the current active.'],
      ['retreat would lose better ramp plan', 'Penalty when staying active preserves a better ramp plan and the retreat does not improve damage.'],
      ['BLOCK cannot afford attack', 'The active Pokemon does not have the energy required for this attack.'],
      ['BLOCK not enough cards in hand to pay attack discard cost', 'The attack requires discarding hand cards that are not available.'],
      ['estimated damage', 'Base score from estimated damage to the opponent\'s active Pokemon.'],
      ['bench/snipe damage', 'Extra value from damage applied to bench or non-active targets.'],
      ['attack KOs current active', 'Large bonus when the attack knocks out the opponent\'s active Pokemon.'],
      ['BLOCK hand-discard attack risks needed evolution in hand', 'The attack would discard cards from hand and may lose an important evolution before securing a KO.'],
      ['attack enables useful EnergyRamp', 'The attack has an EnergyRamp effect and there is a useful ramp target available.'],
      ['energy-discard attack tradeoff', 'Bonus or penalty for attacks that discard attached energy, depending on whether the damage is worth it.'],
      ['offensive debuffs on enemy', 'Value from useful negative effects applied to the opponent.'],
      ['no damage, no ramp, no useful status', 'Penalty for attacks with no direct damage and no meaningful utility.'],
      ['synthetic no-action', 'Logged skip candidate used when AlgorithmBrain intentionally chooses no action.'],
      ['synthetic no-action candidate', 'Dashboard-added skip candidate so the model can assign probability to doing nothing.'],
      ['no reasons', 'Fallback text when a score entry has no recorded scoring terms.']
    ];

    function expertReasonHelpHtml() {
      return `<div class="reason-popover" id="expert-reason-popover" role="dialog" aria-label="Expert reasons help" onclick="event.stopPropagation()">
        <h3>Expert reasons</h3>
        <p>Numbers are score deltas from AlgorithmBrain. BLOCK means the candidate was rejected before normal ranking.</p>
        <dl>${EXPERT_REASON_HELP.map(([reason, description]) => `<dt>${escapeHtml(reason)}</dt><dd>${escapeHtml(description)}</dd>`).join('')}</dl>
      </div>`;
    }

    function toggleExpertReasonHelp(event) {
      event.stopPropagation();
      const root = document.getElementById('expert-reason-help');
      const btn = document.getElementById('expert-reason-info');
      if (!root || !btn) return;
      const pop = document.getElementById('expert-reason-popover');
      if (pop) {
        pop.remove();
        btn.classList.remove('active');
        btn.setAttribute('aria-expanded', 'false');
      } else {
        root.insertAdjacentHTML('beforeend', expertReasonHelpHtml());
        btn.classList.add('active');
        btn.setAttribute('aria-expanded', 'true');
      }
    }

    function closeExpertReasonHelp() {
      const pop = document.getElementById('expert-reason-popover');
      const btn = document.getElementById('expert-reason-info');
      if (pop) pop.remove();
      if (btn) {
        btn.classList.remove('active');
        btn.setAttribute('aria-expanded', 'false');
      }
    }

    document.addEventListener('click', closeExpertReasonHelp);
    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') closeExpertReasonHelp();
    });

    function advisorMeta(e) {
      const parts = [];
      if (e.player_id) parts.push('P' + e.player_id);
      if (e.player_name) parts.push(e.player_name);
      if (e.turn != null) parts.push('turn ' + e.turn);
      if (e.category) parts.push(e.category);
      if (e.action_label) parts.push(e.action_label);
      if (e.confidence != null) parts.push((Number(e.confidence) * 100).toFixed(1) + '%');
      if (e.provider) parts.push(e.provider + (e.model ? '/' + e.model : ''));
      return parts.join(' · ');
    }

    async function loadAdvisorEvents() {
      const feed = document.getElementById('advisor-feed');
      if (!feed) return;
      let data;
      try { data = await api('/api/advisor/events'); } catch (e) { return; }
      const events = data.events || [];
      if (!events.length) {
        feed.innerHTML = '<span class="advisor-empty">No advisor events yet.</span>';
        return;
      }
      feed.innerHTML = events.slice().reverse().map(e => {
        const time = e.timestamp_unix_ms ? new Date(e.timestamp_unix_ms).toLocaleTimeString() : '';
        const advisor = String(e.advisor || '').toLowerCase();
        const meta = advisorMeta(e);
        return `<div class="advisor-event ${advisor}">` +
          `<div class="advisor-time">${escapeHtml(time)}</div>` +
          `<div class="advisor-stage">${escapeHtml(e.advisor || '?')} · ${escapeHtml(e.stage || '?')}</div>` +
          `<div><div class="advisor-message">${escapeHtml(e.message || '')}</div>` +
          `${meta ? `<div class="advisor-meta">${escapeHtml(meta)}</div>` : ''}</div></div>`;
      }).join('');
    }

    async function clearAdvisorEvents() {
      await api('/api/advisor/clear', { method: 'POST' });
      await loadAdvisorEvents();
    }

    // ---- Analysis tab ----
    function fmtNum(v, d = 2) {
      if (v === null || v === undefined || (typeof v === 'number' && !isFinite(v))) return '—';
      if (typeof v !== 'number') return escapeHtml(String(v));
      if (v !== 0 && Math.abs(v) < 1e-4) return v.toExponential(2);
      return v.toFixed(d);
    }
    function fmtP(p) {
      if (p === null || p === undefined) return '—';
      if (p < 1e-4) return p.toExponential(2);
      return p.toFixed(4);
    }
    function corrColor(v) {
      if (v === null || v === undefined) return 'background:var(--panel-2);color:var(--muted)';
      // Diverging blue(neg) – white(0) – magenta(pos).
      const t = Math.max(-1, Math.min(1, v));
      let r, g, b;
      if (t >= 0) { r = 255; g = Math.round(255 - 150 * t); b = Math.round(255 - 80 * t); }
      else { const a = -t; r = Math.round(255 - 160 * a); g = Math.round(255 - 60 * a); b = 255; }
      return `background:rgb(${r},${g},${b})`;
    }
    function descTable(desc) {
      const cols = ['n', 'mean', 'std', 'min', 'q1', 'median', 'q3', 'max', 'iqr', 'skewness', 'kurtosis'];
      const labels = { turns: 'Turns', duration_s: 'Duration (s)', score_margin: 'Score margin', score_total: 'Score total', cards_drawn: 'Cards drawn (per player)' };
      const head = '<tr><th>Variable</th>' + cols.map(c => `<th>${c}</th>`).join('') + '</tr>';
      const body = Object.keys(desc).map(key => {
        const d = desc[key];
        const cells = cols.map(c => {
          if (d.n === 0) return '<td>—</td>';
          if (c === 'n') return `<td>${d.n}</td>`;
          return `<td>${fmtNum(d[c], 2)}</td>`;
        }).join('');
        return `<tr><td>${labels[key] || key}</td>${cells}</tr>`;
      }).join('');
      return `<table class="stat-table"><thead>${head}</thead><tbody>${body}</tbody></table>`;
    }
    let currentStandingsRows = [];
    let currentStandingsMinGames = 0;
    let currentStandingsProfiles = null;   // Set of selected profile names; null until first reconcile (defaults to all)
    let knownStandingsProfiles = new Set(); // every profile seen so far, so new ones default to shown
    let expandedStandingsDecks = new Set(); // decks whose extra profile rows are expanded
    let currentStandingsSort = { key: 'win_rate', dir: 'desc' };
    let analysisLoaded = false; // true after first successful fetch; prevents re-fetch on tab re-entry
    function analysisStandingsMinGames() {
      const el = document.getElementById('analysis-standings-min-games');
      const n = el ? Number.parseInt(el.value || '0', 10) : 0;
      currentStandingsMinGames = Number.isFinite(n) && n > 0 ? n : 0;
      return currentStandingsMinGames;
    }
    function standingsAllProfiles(rows) {
      return [...new Set((rows || []).map(r => String(r.profile || 'Unknown')))]
        .sort((a, b) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' }));
    }
    // Reconcile the selection set with the profiles currently present: initialise to all,
    // keep new (never-seen) profiles shown by default, and drop selections that no longer exist.
    function reconcileStandingsProfiles(rows) {
      const all = standingsAllProfiles(rows);
      if (currentStandingsProfiles === null) currentStandingsProfiles = new Set(all);
      for (const p of all) if (!knownStandingsProfiles.has(p)) currentStandingsProfiles.add(p);
      currentStandingsProfiles = new Set(all.filter(p => currentStandingsProfiles.has(p)));
      knownStandingsProfiles = new Set(all);
      return all;
    }
    function standingsProfileCheckboxes(rows) {
      const all = reconcileStandingsProfiles(rows);
      if (!all.length) return '<span class="subtle">No profiles yet.</span>';
      const boxes = all.map(p => {
        const checked = currentStandingsProfiles.has(p) ? ' checked' : '';
        return `<label class="inline-check"><input type="checkbox" class="standings-profile-cb" value="${escapeHtml(p)}"${checked} onchange="onStandingsProfileToggle()"> ${escapeHtml(p)}</label>`;
      }).join('');
      return `<button class="stat-sort" type="button" onclick="setStandingsProfiles(true)">All</button>`
        + `<button class="stat-sort" type="button" onclick="setStandingsProfiles(false)">None</button>`
        + boxes;
    }
    function renderStandingsProfileControls() {
      const box = document.getElementById('analysis-standings-profiles');
      if (box) box.innerHTML = standingsProfileCheckboxes(currentStandingsRows);
    }
    function onStandingsProfileToggle() {
      const sel = new Set();
      document.querySelectorAll('.standings-profile-cb').forEach(cb => { if (cb.checked) sel.add(cb.value); });
      currentStandingsProfiles = sel;
      updateStandingsFilter();
    }
    function setStandingsProfiles(all) {
      currentStandingsProfiles = all ? new Set(standingsAllProfiles(currentStandingsRows)) : new Set();
      renderStandingsProfileControls();
      updateStandingsFilter();
    }
    function toggleStandingsDeck(deck) {
      const d = String(deck || '');
      if (expandedStandingsDecks.has(d)) expandedStandingsDecks.delete(d);
      else expandedStandingsDecks.add(d);
      updateStandingsFilter();
    }
    // Export the current (filtered, sorted) standings to a plain-text file. Includes every
    // profile per deck — the best is marked with '*', the rest indented under it.
    function exportStandingsTxt() {
      const note = document.getElementById('scan-note');
      const rows = currentStandingsRows;
      if (!rows || !rows.length) { if (note) note.textContent = 'No standings to export yet.'; return; }
      const minGames = analysisStandingsMinGames();  // read the live "Min games" input
      const profiles = currentStandingsProfiles;     // current profile-checkbox selection
      const { filtered, showAll, groups } = standingsFilteredGroups(rows, minGames, profiles);
      if (!filtered.length) { if (note) note.textContent = 'No standings rows match the current filters.'; return; }
      const fmtWr = r => (r.win_rate != null ? (r.win_rate * 100).toFixed(1) + '%' : '—');
      // Flatten to printable rows: rep first (marked best), then the deck's other profiles.
      const flat = [];
      groups.forEach(g => {
        flat.push({ deck: String(g.rep.deck || ''), best: g.others.length > 0, r: g.rep });
        g.others.forEach(r => flat.push({ deck: '', best: false, r }));
      });
      const cols = [
        { h: 'Deck',    get: x => x.deck },
        { h: 'Profile', get: x => (x.best ? '* ' : (x.deck ? '' : '  ')) + String(x.r.profile || '') },
        { h: 'W',       get: x => String(x.r.wins ?? '') , right: true },
        { h: 'L',       get: x => String(x.r.losses ?? ''), right: true },
        { h: 'D',       get: x => String(x.r.draws ?? '') , right: true },
        { h: 'Games',   get: x => String(x.r.games ?? '') , right: true },
        { h: 'WinRate', get: x => fmtWr(x.r),               right: true },
      ];
      const widths = cols.map(c => Math.max(c.h.length, ...flat.map(x => c.get(x).length)));
      const pad = (s, w, right) => right ? s.padStart(w) : s.padEnd(w);
      const line = vals => vals.map((v, i) => pad(v, widths[i], cols[i].right)).join('  ').replace(/\s+$/, '');
      const lines = [];
      lines.push('Standings (by win rate)');
      lines.push('Exported ' + new Date().toISOString().replace('T', ' ').slice(0, 19));
      const fparts = [];
      fparts.push('profiles: ' + (showAll ? 'all' : [...profiles].sort().join(', ')));
      if (minGames > 0) fparts.push('min games: ' + minGames);
      lines.push('Filters: ' + fparts.join(' | '));
      lines.push('Decks: ' + groups.length + '  (profile rows: ' + filtered.length + ')');
      lines.push('');
      lines.push(line(cols.map(c => c.h)));
      lines.push(widths.map(w => '-'.repeat(w)).join('  '));
      flat.forEach(x => lines.push(line(cols.map(c => c.get(x)))));
      const text = lines.join('\n') + '\n';
      const stamp = new Date().toISOString().slice(0, 19).replace(/[-:T]/g, '');
      const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url; a.download = 'standings_' + stamp + '.txt';
      document.body.appendChild(a); a.click(); a.remove();
      URL.revokeObjectURL(url);
      if (note) note.textContent = 'Exported ' + groups.length + ' deck(s) to standings_' + stamp + '.txt';
    }
    function standingsSortValue(row, key) {
      if (['wins', 'losses', 'draws', 'games', 'win_rate'].includes(key)) {
        const value = Number(row[key]);
        return Number.isFinite(value) ? value : -Infinity;
      }
      return String(row[key] ?? '').toLocaleLowerCase();
    }
    function sortedStandingsRows(rows) {
      const { key, dir } = currentStandingsSort;
      const sign = dir === 'asc' ? 1 : -1;
      return rows.slice().sort((a, b) => {
        const av = standingsSortValue(a, key);
        const bv = standingsSortValue(b, key);
        let cmp = 0;
        if (typeof av === 'number' && typeof bv === 'number') {
          cmp = av === bv ? 0 : (av < bv ? -1 : 1);
        } else {
          cmp = String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: 'base' });
        }
        if (cmp === 0 && key !== 'win_rate') {
          const awr = standingsSortValue(a, 'win_rate');
          const bwr = standingsSortValue(b, 'win_rate');
          cmp = awr === bwr ? 0 : (awr < bwr ? -1 : 1);
        }
        if (cmp === 0) {
          cmp = String(a.deck ?? '').localeCompare(String(b.deck ?? ''), undefined, { numeric: true, sensitivity: 'base' });
        }
        return cmp * sign;
      });
    }
    function standingsSortHeader(key, label) {
      const active = currentStandingsSort.key === key;
      const indicator = active ? (currentStandingsSort.dir === 'asc' ? '^' : 'v') : '';
      return `<th><button class="stat-sort" onclick="sortStandings('${key}')">${label}<span class="sort-indicator">${indicator}</span></button></th>`;
    }
    function sortStandings(key) {
      currentStandingsSort = currentStandingsSort.key === key
        ? { key, dir: currentStandingsSort.dir === 'asc' ? 'desc' : 'asc' }
        : { key, dir: ['deck', 'profile'].includes(key) ? 'asc' : 'desc' };
      updateStandingsFilter();
    }
    // Shared filter + group-by-deck logic for both the table and the .txt export.
    // Returns the rows passing the filters and an ordered list of groups, each with the
    // representative (highest win-rate) row and the deck's other profile rows.
    function standingsFilteredGroups(rows, minGames, profiles) {
      const allProfiles = standingsAllProfiles(rows);
      const showAll = !profiles || profiles.size >= allProfiles.length;
      const filtered = (rows || []).filter(r =>
        (Number(r.games) || 0) >= minGames &&
        (showAll || (profiles && profiles.has(String(r.profile || 'Unknown'))))
      );
      const wrOf = r => (r.win_rate == null ? -Infinity : r.win_rate);
      const repOf = deckRows => deckRows.slice().sort((a, b) => {
        if (wrOf(a) !== wrOf(b)) return wrOf(b) - wrOf(a);                 // highest win rate first
        if ((b.games || 0) !== (a.games || 0)) return (b.games || 0) - (a.games || 0);  // then more games
        return String(a.profile || '').localeCompare(String(b.profile || ''));
      })[0];
      const byDeck = new Map();
      filtered.forEach(r => {
        const d = String(r.deck || '');
        if (!byDeck.has(d)) byDeck.set(d, []);
        byDeck.get(d).push(r);
      });
      const built = [...byDeck.entries()].map(([deck, deckRows]) => {
        const rep = repOf(deckRows);
        const others = deckRows.filter(r => r !== rep).sort((a, b) => wrOf(b) - wrOf(a));
        return { deck, deckRows, rep, others };
      });
      const byDeckMap = new Map(built.map(g => [g.deck, g]));
      const groups = sortedStandingsRows(built.map(g => g.rep)).map(rep => byDeckMap.get(String(rep.deck || '')));
      return { filtered, showAll, allProfiles, groups };
    }
    function standingsTable(rows, minGames = 0, profiles = null) {
      if (!rows || !rows.length) return '<span class="subtle">No games logged yet.</span>';
      if (profiles && profiles.size === 0) {
        return '<span class="subtle">No profiles selected — tick at least one profile to show.</span>';
      }
      const { filtered, showAll, groups } = standingsFilteredGroups(rows, minGames, profiles);
      if (!filtered.length) {
        const profileText = showAll ? '' : ` for the selected profile(s)`;
        return `<span class="subtle">No standings rows${profileText} have at least ${escapeHtml(String(minGames))} games.</span>`;
      }
      const head = '<tr>' +
        standingsSortHeader('deck', 'Deck') +
        standingsSortHeader('profile', 'Profile') +
        standingsSortHeader('wins', 'W') +
        standingsSortHeader('losses', 'L') +
        standingsSortHeader('draws', 'D') +
        standingsSortHeader('games', 'Games') +
        standingsSortHeader('win_rate', 'Win rate') +
        '</tr>';
      const cells = r => {
        const wr = r.win_rate != null ? (r.win_rate * 100).toFixed(1) + '%' : '—';
        return `<td>${r.wins}</td><td>${r.losses}</td><td>${r.draws}</td><td>${r.games}</td><td>${wr}</td>`;
      };
      const body = groups.map(g => {
        const rep = g.rep;
        const others = g.others;
        const expandable = others.length > 0;
        const expanded = expandedStandingsDecks.has(String(rep.deck || ''));
        const deckArg = escapeHtml(JSON.stringify(String(rep.deck || '')));
        const toggle = expandable
          ? `<button class="stat-sort" type="button" title="Show this deck's other profiles" onclick="toggleStandingsDeck(${deckArg})" style="margin-right:6px">${expanded ? '▾' : '▸'}</button>`
          : `<span style="display:inline-block; width:18px"></span>`;
        const badge = expandable ? ` <span class="subtle">(${g.deckRows.length})</span>` : '';
        let html = `<tr><td>${toggle}${escapeHtml(rep.deck)}${badge}</td><td>${escapeHtml(rep.profile)}</td>${cells(rep)}</tr>`;
        if (expandable && expanded) {
          html += others.map(r =>
            `<tr class="standings-child"><td style="padding-left:30px" class="subtle">↳</td><td>${escapeHtml(r.profile)}</td>${cells(r)}</tr>`
          ).join('');
        }
        return html;
      }).join('');
      const activeFilters = [];
      if (!showAll) activeFilters.push(`${profiles.size} profile(s)`);
      if (minGames > 0) activeFilters.push(`at least ${minGames} games`);
      const filterNote = activeFilters.length ? ` with ${activeFilters.join(' and ')}` : '';
      const note = `<p class="test-stats">Showing ${groups.length} deck(s) from ${filtered.length} profile row(s)${filterNote}. Each deck shows its highest win-rate profile; ▸ expands the rest.</p>`;
      return `${note}<table class="stat-table"><thead>${head}</thead><tbody>${body}</tbody></table>`;
    }
    function updateStandingsFilter() {
      const target = document.getElementById('analysis-standings-table');
      if (!target) return;
      target.innerHTML = standingsTable(currentStandingsRows, analysisStandingsMinGames(), currentStandingsProfiles);
    }
    function standingsPanel(rows) {
      currentStandingsRows = Array.isArray(rows) ? rows : [];
      const minGames = currentStandingsMinGames;
      const profileCheckboxes = standingsProfileCheckboxes(currentStandingsRows);
      return `<div class="panel"><div class="row" style="align-items:center; justify-content:space-between; gap:10px">` +
        `<h2 style="margin:0">Standings (by win rate)</h2>` +
        `<div class="row" style="align-items:center; gap:8px">` +
          `<span class="subtle" title="Tick which AlgorithmBrain profiles appear in the table.">Profiles:</span>` +
          `<div id="analysis-standings-profiles" class="row" style="align-items:center; gap:8px; flex-wrap:wrap">${profileCheckboxes}</div>` +
          `<label class="subtle" style="display:flex; align-items:center; gap:6px" title="Hide deck/profile rows with fewer games than this threshold.">Min games` +
            `<input id="analysis-standings-min-games" type="number" min="0" step="1" value="${escapeHtml(String(minGames))}" oninput="updateStandingsFilter()" style="width:96px; min-width:96px">` +
          `</label>` +
          `<button class="secondary" type="button" title="Download the current (filtered) standings as a .txt file, with each deck's profiles listed." onclick="exportStandingsTxt()">⬇ Export .txt</button>` +
        `</div></div>` +
        `<div id="analysis-standings-table" style="margin-top:12px">${standingsTable(currentStandingsRows, minGames, currentStandingsProfiles)}</div></div>`;
    }
    function corrTable(corr) {
      if (!corr) return '<span class="subtle">Not enough data.</span>';
      const L = corr.labels;
      const head = '<tr><th></th>' + L.map(l => `<th>${escapeHtml(l)}</th>`).join('') + '</tr>';
      const rows = corr.matrix.map((row, i) =>
        `<tr><th>${escapeHtml(L[i])}</th>` + row.map((v, j) =>
          j > i
            ? '<td class="corr-empty"></td>'  // upper triangle mirrors the lower half; hide it
            : `<td style="${corrColor(v)}">${v === null ? '—' : v.toFixed(2)}</td>`).join('') + '</tr>'
      ).join('');
      return `<table class="corr-grid"><thead>${head}</thead><tbody>${rows}</tbody></table>`;
    }
    function histChart(h) {
      if (!h || !h.counts.length) return '<span class="subtle">No data.</span>';
      const max = Math.max(...h.counts);
      const bars = h.counts.map((c, i) => {
        const pct = max > 0 ? (c / max * 100) : 0;
        const lo = h.edges[i], hi = h.edges[i + 1];
        return `<div class="bar" style="height:${pct}%" title="[${lo.toFixed(1)}, ${hi.toFixed(1)}): ${c}">` +
          `<span>${c || ''}</span></div>`;
      }).join('');
      const e = h.edges;
      return `<div class="hist">${bars}</div>` +
        `<div class="hist-axis"><span>${e[0].toFixed(0)}</span><span>${e[e.length - 1].toFixed(0)} turns</span></div>`;
    }
    function testCard(title, sig, verdict, stats) {
      const cls = sig ? 'sig' : 'nsig';
      const verdictHtml = verdict ? `<div class="test-verdict">${escapeHtml(verdict)}</div>` : '';
      return `<div class="test-card ${cls}"><div class="advisor-stage">${escapeHtml(title)}</div>` +
        verdictHtml +
        `<div class="test-stats">${stats}</div></div>`;
    }
    function firstPlayerConclusion(fp) {
      if (!fp || !fp.significant) return 'Conclusion: no clear first-player advantage in this sample.';
      if (fp.a_win_rate > 0.5) return 'Conclusion: seat A has a measurable first-player advantage.';
      if (fp.a_win_rate < 0.5) return 'Conclusion: seat A is measurably disadvantaged.';
      return 'Conclusion: seat A is not different from 50%.';
    }
    function normalityConclusion(nt) {
      if (!nt || nt.statistic === null) return '';
      return nt.normal
        ? 'Conclusion: game length looks normal enough in this sample.'
        : 'Conclusion: game length is not normally distributed.';
    }
    function profileConclusion(pw) {
      if (!pw || !pw.test || pw.test.p_value == null) return '';
      return pw.test.p_value < 0.05
        ? 'Conclusion: win-rate differs by algorithm profile.'
        : 'Conclusion: no clear profile effect on win-rate.';
    }
    function usageArt(card) {
      const name = (card && card.name) || '?';
      const initials = name.split(/\s+/).filter(Boolean).slice(0, 2).map(s => s[0]).join('').toUpperCase() || '?';
      if (card && card.image_url) {
        return `<div class="usage-art"><img src="${escapeHtml(card.image_url)}" alt="${escapeHtml(name)}"></div>`;
      }
      return `<div class="usage-art">${escapeHtml(initials)}</div>`;
    }
    function cardText(card) {
      if (!card) return '';
      const bits = [];
      if (card.description) bits.push(card.description);
      if (card.attacks && card.attacks.length) {
        const a = card.attacks[0];
        const dmg = a.damage !== null && a.damage !== undefined ? ` · ${a.damage} dmg` : '';
        bits.push(`${a.name || 'Attack'}${dmg}`);
      }
      if (card.effects && card.effects.length) bits.push(card.effects.join(', '));
      return bits.filter(Boolean).slice(0, 2).join(' · ');
    }
    function usageHero(title, item, metricLabel) {
      if (!item || !item.card) {
        return `<div class="usage-hero"><div class="usage-art">—</div><div><div class="usage-subtitle">${escapeHtml(title)}</div><div class="usage-title">No data</div></div></div>`;
      }
      const card = item.card;
      return `<div class="usage-hero">${usageArt(card)}<div>` +
        `<div class="usage-subtitle">${escapeHtml(title)}</div>` +
        `<div class="usage-title">${escapeHtml(card.name || 'Unknown')}</div>` +
        `<div class="usage-metric">${escapeHtml(String(item.count ?? '—'))} <span class="test-stats">${escapeHtml(metricLabel)}</span></div>` +
        `<div class="usage-desc">${escapeHtml(card.subtitle || '')}${cardText(card) ? '<br>' + escapeHtml(cardText(card)) : ''}</div>` +
        `</div></div>`;
    }
    function biggestHitHero(hit) {
      if (!hit) {
        return `<div class="usage-hero"><div class="usage-art">—</div><div><div class="usage-subtitle">Biggest logged damage</div><div class="usage-title">No attack data</div></div></div>`;
      }
      const card = hit.card_info || { name: hit.card };
      const damage = Number(hit.damage || 0);
      const hitCount = Number(hit.hit_count || 1);
      const perHit = Number(hit.per_hit_damage || damage);
      const base = Number(hit.base_damage || damage);
      const effectBits = (hit.effect_names || []).length ? ` · effects: ${(hit.effect_names || []).join(', ')}` : '';
      const damageLine = hitCount > 1
        ? `${fmtNum(perHit, perHit % 1 ? 1 : 0)} x ${hitCount} hits · base ${fmtNum(base, base % 1 ? 1 : 0)}`
        : `base ${fmtNum(base, base % 1 ? 1 : 0)}${effectBits}`;
      return `<div class="usage-hero">${usageArt(card)}<div>` +
        `<div class="usage-subtitle">Biggest estimated attack damage</div>` +
        `<div class="usage-title">${escapeHtml(hit.card || 'Unknown')} — ${escapeHtml(hit.attack || 'Attack')}</div>` +
        `<div class="usage-metric">${fmtNum(damage, damage % 1 ? 1 : 0)} <span class="test-stats">damage</span></div>` +
        `<div class="usage-desc">Turn ${escapeHtml(String(hit.turn ?? '—'))} · Player ${escapeHtml(String(hit.player_id ?? '—'))} · target ${escapeHtml(hit.target || 'opponent active')}<br>${escapeHtml(damageLine)}${hitCount <= 1 && effectBits ? '' : escapeHtml(effectBits)}<br>${escapeHtml(cardText(card))}</div>` +
        `</div></div>`;
    }
    function usageRank(items, metricLabel = 'logged uses') {
      if (!items || !items.length) return '<span class="subtle">No card usage found in decision logs.</span>';
      return `<div class="usage-rank">` + items.map((it, i) =>
        `<div class="usage-rank-row"><span class="test-stats">#${i + 1}</span><strong>${escapeHtml((it.card && it.card.name) || 'Unknown')}</strong><span class="usage-rank-count">${escapeHtml(String(it.count))} ${escapeHtml(metricLabel)}</span></div>`
      ).join('') + `</div>`;
    }
    function cardUsagePanel(cu) {
      if (!cu) return '';
      const top = cu.top_cards || [];
      const attackers = cu.top_attackers || [];
      const cat = cu.category_counts || {};
      const catText = Object.keys(cat).sort((a, b) => cat[b] - cat[a]).map(k => `${k}: ${cat[k]}`).join(' · ');
      const notes = (cu.notes || []).map(n => escapeHtml(n)).join('<br>');
      return `<div class="panel"><h2>Card usage insights</h2>` +
        `<div class="card-usage-grid">` +
          `${usageHero('Most logged card reference', cu.most_used, 'logged references')}` +
          `${usageHero('Most directly played card', cu.most_played, 'direct plays')}` +
          `${biggestHitHero(cu.biggest_hit)}` +
          `${usageHero('Least logged card reference', cu.least_used, 'logged reference(s)')}` +
        `</div>` +
        `<div class="analysis-grid" style="margin-top:14px">` +
          `<div><h2>Most logged card references</h2>${usageRank(top, 'references')}</div>` +
          `<div><h2>Most frequent attack sources</h2>${usageRank(attackers, 'attacks')}</div>` +
        `</div>` +
        `<div class="usage-note">Decision records scanned: ${escapeHtml(String(cu.decision_records || 0))} · card-linked actions: ${escapeHtml(String(cu.card_usage_records || 0))} · cards seen: ${escapeHtml(String(cu.cards_seen || 0))}<br>Logged references count every card-linked decision record. Direct plays count only Basic/Evolution/Trainer play actions. Attack sources count Attack decisions attributed to the active Pokemon.${catText ? '<br>' + escapeHtml(catText) : ''}${notes ? '<br>' + notes : ''}</div>` +
      `</div>`;
    }

    async function loadAnalysis() {
      const body = document.getElementById('analysis-body');
      const statusEl = document.getElementById('analysis-status');
      if (!body) return;
      const sourceSel = document.getElementById('analysis-source');
      const source = sourceSel ? sourceSel.value : 'all';
      const matchupSel = document.getElementById('analysis-matchup');
      const matchup = matchupSel ? matchupSel.value : 'all';
      currentStandingsMinGames = analysisStandingsMinGames();
      const refreshBtn = document.getElementById('analysis-refresh-btn');
      const previousBtnText = refreshBtn ? refreshBtn.textContent : '';
      const filterNote = [source !== 'all' ? source : '', matchup !== 'all' ? matchup : ''].filter(Boolean).join(' · ');
      body.innerHTML = `<div class="panel loading-panel"><span class="loading-dot"></span><span>Loading analysis statistics${filterNote ? ' for ' + escapeHtml(filterNote) : ''}…</span></div>`;
      if (statusEl) statusEl.textContent = 'Loading statistics…';
      if (refreshBtn) {
        refreshBtn.disabled = true;
        refreshBtn.textContent = 'Loading…';
      }
      let r;
      try { r = await api('/api/analysis?source=' + encodeURIComponent(source) + '&matchup=' + encodeURIComponent(matchup)); }
      catch (e) {
        body.innerHTML = `<div class="panel"><span class="bad">Failed to load analysis.</span></div>`;
        if (statusEl) statusEl.textContent = 'Load failed';
        return;
      } finally {
        if (refreshBtn) {
          refreshBtn.disabled = false;
          refreshBtn.textContent = previousBtnText || 'Refresh';
        }
      }
      // Rebuild the matchup dropdown from the available matchups for the current source view.
      if (matchupSel && Array.isArray(r.matchups)) {
        const want = matchup;
        const opts = ['<option value="all">All matchups</option>']
          .concat(r.matchups.map(m => `<option value="${escapeHtml(m.key)}">${escapeHtml(m.key)} (${m.games})</option>`));
        matchupSel.innerHTML = opts.join('');
        matchupSel.value = Array.from(matchupSel.options).some(o => o.value === want) ? want : 'all';
      }
      const exParts = [];
      if (r.n_excluded_by_source) exParts.push(`${r.n_excluded_by_source} excluded by source`);
      if (r.n_excluded_by_matchup) exParts.push(`${r.n_excluded_by_matchup} excluded by matchup`);
      const excludedNote = exParts.length ? ' · ' + exParts.join(' · ') : '';
      if (!r.ok) {
        body.innerHTML = `<div class="panel"><span class="subtle">${escapeHtml(r.reason || 'No data')}. Run a benchmark first.</span></div>`;
        if (statusEl) statusEl.textContent = excludedNote ? excludedNote.replace(/^ · /, '') : '';
        return;
      }
      if (statusEl) {
        const deckAttr = r.n_deck_attributed ?? 0;
        const profileAttr = r.n_profile_attributed ?? r.n_attributed ?? 0;
        statusEl.textContent = `${r.n_games} games · ${r.n_decided} decided · ${deckAttr} deck-attributed · ${profileAttr} profile-attributed${excludedNote}`;
      }

      // First-player hypothesis test.
      const fp = r.first_player;
      const fpCard = testCard('First-player advantage',
        fp.significant, firstPlayerConclusion(fp),
        `Two-sided binomial test · A wins <strong>${fp.a_wins}</strong> / <strong>${fp.n}</strong> · win-rate <strong>${(fp.a_win_rate * 100).toFixed(1)}%</strong> · p-value = <strong>${fmtP(fp.p_value)}</strong> · α = 0.05`);

      // Normality test.
      const nt = r.normality_turns;
      const ntCard = (nt.statistic === null)
        ? ''
        : testCard('Game length shape',
            !nt.normal, normalityConclusion(nt),
            `Jarque–Bera test · JB = ${fmtNum(nt.statistic, 3)} · p-value = <strong>${fmtP(nt.p_value)}</strong> · skewness = <strong>${fmtNum(nt.skewness, 3)}</strong> · excess kurtosis = <strong>${fmtNum(nt.excess_kurtosis, 3)}</strong>`);

      // Profile win-rate + chi-square.
      let profileHtml = '<span class="subtle">No heterogeneous profile match-ups logged yet.</span>';
      const pw = r.profile_winrate;
      if (pw && pw.profiles && pw.profiles.length) {
        const rows = pw.profiles.map(p => {
          const pct = p.win_rate != null ? (p.win_rate * 100) : 0;
          return `<div class="wr-row"><div>${escapeHtml(p.profile)}</div>` +
            `<div class="wr-bar-track"><div class="wr-bar-fill" style="width:${pct.toFixed(0)}%"></div></div>` +
            `<div class="test-stats">${pct.toFixed(1)}% (${p.wins}/${p.games})</div></div>`;
        }).join('');
        let testHtml = '';
        if (pw.test && pw.test.p_value != null) {
          const sig = pw.test.p_value < 0.05;
          testHtml = testCard('Profile effect on win-rate',
            sig, profileConclusion(pw),
            `Chi-square test · χ² = <strong>${fmtNum(pw.test.chi2, 3)}</strong> · df = ${pw.test.df} · p-value = <strong>${fmtP(pw.test.p_value)}</strong>`);
        }
        profileHtml = rows + testHtml;
      }

      // End reasons (storytelling).
      const er = r.end_reasons || {};
      const erTotal = Object.values(er).reduce((a, b) => a + b, 0) || 1;
      const erHtml = Object.keys(er).sort((a, b) => er[b] - er[a]).map(k =>
        `<div class="wr-row"><div>${escapeHtml(k)}</div>` +
        `<div class="wr-bar-track"><div class="wr-bar-fill" style="width:${(er[k] / erTotal * 100).toFixed(0)}%"></div></div>` +
        `<div class="test-stats">${er[k]} (${(er[k] / erTotal * 100).toFixed(1)}%)</div></div>`
      ).join('');

      const ot = r.outliers_turns;
      const otHtml = (ot && ot.lower_fence != null)
        ? `Tukey fences [${ot.lower_fence.toFixed(1)}, ${ot.upper_fence.toFixed(1)}] · ${ot.count} outlier game(s)`
        : 'Not enough data for outlier fences.';

      body.innerHTML =
        `<div class="panel"><h2>Descriptive statistics (EDA)</h2>${descTable(r.descriptive)}` +
        `<p class="test-stats" style="margin-top:8px">Outliers (game length, IQR rule): ${otHtml}</p>` +
        `<p class="subtle" style="margin-top:6px"><code>duration_s</code> is wall-clock time per game (hardware- and LLM-latency dependent) — a compute cost, not a gameplay metric; it is excluded from the correlation matrices.</p></div>` +
        `<div class="analysis-grid">` +
          `<div class="panel"><h2>Game-length distribution</h2>${histChart(r.histogram_turns)}</div>` +
          `<div class="panel"><h2>Correlation (Pearson)</h2>${corrTable(r.correlation_pearson)}` +
            `<h2 style="margin-top:14px">Correlation (Spearman, rank)</h2>${corrTable(r.correlation_spearman)}</div>` +
        `</div>` +
        `${standingsPanel(r.deck_winrate)}` +
        `<div class="analysis-grid">` +
          `<div class="panel"><h2>Win-rate by algorithm profile</h2>${profileHtml}</div>` +
          `<div class="panel"><h2>Outcome / end-reason mix</h2>${erHtml}</div>` +
        `</div>` +
        `<div class="panel"><h2>Hypothesis tests</h2>${fpCard}${ntCard}</div>` +
        `${cardUsagePanel(r.card_usage)}`;
      analysisLoaded = true;
    }

    async function loadAdvisorModelPicker() {
      const sel = document.getElementById('advisor-model-select');
      const statusEl = document.getElementById('advisor-model-status');
      if (!sel) return;
      let data;
      try { data = await api('/api/models'); }
      catch (e) { sel.innerHTML = '<option value="">Failed to list models</option>'; return; }
      const models = (data.models || []).slice().sort((a, b) => (b.trained_unix_ms || b.modified_unix_ms) - (a.trained_unix_ms || a.modified_unix_ms));
      const loaded = lastStatus.loaded_model || '';
      if (!models.length) {
        sel.innerHTML = '<option value="">No .pt models found</option>';
        if (statusEl) statusEl.textContent = '';
        return;
      }
      sel.innerHTML = '<option value="">— none (API disabled) —</option>' + models.map(m => {
        const name = m.path.split(/[\\/]/).pop();
        const macro = m.meta && m.meta.val_macro_acc != null ? ` · macro ${(m.meta.val_macro_acc * 100).toFixed(1)}%` : '';
        const acc = m.meta && m.meta.val_acc != null ? ` · val ${(m.meta.val_acc * 100).toFixed(1)}%` : '';
        return `<option value="${escapeHtml(m.path)}"${m.path === loaded ? ' selected' : ''}>${escapeHtml(name)}${acc}${macro}</option>`;
      }).join('');
      if (statusEl) {
        statusEl.innerHTML = loaded
          ? `serving <span class="mono">${escapeHtml(loaded.split(/[\\/]/).pop())}</span>`
          : '<span class="hv bad">no model loaded — /predict is offline</span>';
      }
    }

    async function setApiModel(path) {
      const statusEl = document.getElementById('advisor-model-status');
      if (!path) { if (statusEl) statusEl.textContent = 'No model selected — /predict stays on the previously loaded model.'; return; }
      if (statusEl) statusEl.textContent = 'Loading…';
      try {
        await api('/api/load-model', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ path }) });
      } catch (e) { if (statusEl) statusEl.innerHTML = `<span class="hv bad">Load failed: ${escapeHtml(e.message)}</span>`; return; }
      await refresh();
      await loadAdvisorModelPicker();
      loadModels();
    }

    let lossChart = null, accChart = null;
    function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }
    function chartDefaults() {
      const muted = cssVar('--muted') || '#9fb3c7';
      const grid = cssVar('--line') || 'rgba(154,178,205,.12)';
      return {
        responsive: true, maintainAspectRatio: false,
        plugins: { legend: { labels: { color: muted, font: { size: 11 } } } },
        scales: {
          x: { ticks: { color: muted, font: { size: 10 } }, grid: { color: grid } },
          y: { ticks: { color: muted, font: { size: 10 } }, grid: { color: grid } }
        }
      };
    }

    const RUN_PALETTE = ['#43d9bd', '#66a6ff', '#f5bd4f', '#f06478', '#5ee48f', '#b58cff', '#ff9d5c', '#4fd0e0'];
    function shortRunName(name) { return String(name).replace(/^train_/, '').replace(/^model:/, ''); }

    const stageMarkerPlugin = {
      id: 'stageMarker',
      afterDatasetsDraw(chart, _args, opts) {
        const boundary = opts && Number(opts.boundaryIndex);
        if (!Number.isFinite(boundary) || boundary <= 0) return;
        const xScale = chart.scales.x;
        const area = chart.chartArea;
        if (!xScale || !area || boundary >= chart.data.labels.length) return;
        const x = xScale.getPixelForValue(boundary - 0.5);
        const ctx = chart.ctx;
        const color = cssVar('--magenta') || '#e36bff';
        ctx.save();
        ctx.strokeStyle = color;
        ctx.setLineDash([5, 4]);
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x, area.top);
        ctx.lineTo(x, area.bottom);
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.fillStyle = color;
        ctx.font = '11px system-ui, sans-serif';
        ctx.textBaseline = 'top';
        ctx.fillText(opts.label || 'fine-tune starts', Math.min(x + 6, area.right - 92), area.top + 6);
        ctx.restore();
      }
    };
    try { if (window.Chart) Chart.register(stageMarkerPlugin); } catch (e) {}

    function drawCharts(labels, lossDatasets, accDatasets, meta = {}) {
      const base = chartDefaults();
      const mk = (el, datasets) => {
        const ctx = document.getElementById(el).getContext('2d');
        const cfg = {
          type: 'line',
          data: { labels, datasets },
          options: {
            ...base,
            scales: { ...base.scales, y: { ...base.scales.y, title: { display: false } } },
            plugins: {
              ...base.plugins,
              stageMarker: {
                boundaryIndex: meta.stage_boundary_index,
                label: meta.stage_boundary_label || 'fine-tune starts'
              }
            }
          }
        };
        return new Chart(ctx, cfg);
      };
      if (lossChart) lossChart.destroy();
      if (accChart) accChart.destroy();
      lossChart = mk('chart-loss', lossDatasets);
      accChart = mk('chart-acc', accDatasets);
    }

    function showCharts(show, note) {
      document.getElementById('chart-empty').style.display = show ? 'none' : '';
      document.querySelector('.charts').style.display = show ? '' : 'none';
      stage('stage-metrics', show ? 'done' : 'idle', note);
    }

    let metricsSig = null;
    let pendingFineTuneCompare = null;
    function selectedRunNames() {
      return Array.from(document.querySelectorAll('#run-picker input[type=checkbox]:checked')).map(c => c.value);
    }
    async function currentMetricRuns() {
      try {
        const data = await api('/api/runs');
        return data.runs || [];
      } catch (e) {
        return [];
      }
    }
    async function baselineRunForFineTune(fromModel, runs = null) {
      const allRuns = runs || await currentMetricRuns();
      if (!allRuns.length) return null;
      let modelPath = fromModel;
      if (!modelPath || modelPath === 'latest') {
        try {
          const data = await api('/api/models');
          modelPath = (data.models || [])[0]?.path || '';
        } catch (e) {
          modelPath = '';
        }
      }
      const match = modelPath
        ? allRuns.find(r => r.model_path === modelPath || (r.model_path && r.model_path.split(/[\\/]/).pop() === modelPath.split(/[\\/]/).pop()))
        : null;
      return (match || allRuns[0]).name;
    }
    function clearRunSelection() {
      document.querySelectorAll('#run-picker input[type=checkbox]').forEach(c => { c.checked = false; });
      pendingFineTuneCompare = null;
      metricsSig = null;
      loadMetrics();
    }
    async function loadRunPicker() {
      const picker = document.getElementById('run-picker');
      if (!picker) return;
      let data;
      try { data = await api('/api/runs'); } catch (e) { return; }
      const runs = data.runs || [];
      const checked = new Set(selectedRunNames());
      if (!runs.length) { picker.innerHTML = ''; return; }
      let resolvedFineTuneCompare = false;
      if (pendingFineTuneCompare) {
        const baseRun = pendingFineTuneCompare.baseRun;
        const knownRuns = pendingFineTuneCompare.knownRuns || new Set([baseRun]);
        const fineTuneRun = runs.find(r => !knownRuns.has(r.name));
        if (baseRun && fineTuneRun) {
          checked.clear();
          checked.add(baseRun);
          checked.add(fineTuneRun.name);
          pendingFineTuneCompare = null;
          metricsSig = null;
          resolvedFineTuneCompare = true;
        }
      }
      picker.innerHTML = runs.map(r => {
        const on = checked.has(r.name) ? 'checked' : '';
        // Sidecar-only runs (synced models with no train log) carry no deletable log artifact.
        const del = r.log_backed === false ? '' : `<button class="danger" title="Delete this metrics run log" onclick="deleteRun('${r.name}')">x</button>`;
        const tag = r.log_backed === false ? ' <span class="subtle" title="from model sidecar (no train log)">[synced]</span>' : '';
        return `<span class="run-chip"><label class="inline-check"><input type="checkbox" value="${r.name}" ${on} onchange="metricsSig=null; loadMetrics()"> ${shortRunName(r.name)} <span class="subtle">(${r.epochs}ep)</span>${tag}</label>${del}</span>`;
      }).join('');
      if (resolvedFineTuneCompare) loadMetrics();
    }

    async function deleteRun(name) {
      if (!confirm('Delete metrics run "' + shortRunName(name) + '"? This removes the training log and matching run artifact folder if one exists.')) return;
      try {
        await api('/api/delete-run', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({ name }) });
      } catch (e) { alert('Run delete failed: ' + e.message); return; }
      metricsSig = null;
      await loadRunPicker();
      await loadMetrics();
    }

    async function loadMetrics() {
      const selected = selectedRunNames();
      if (selected.length) return loadMetricsCompare(selected);

      let data;
      try { data = await api('/api/metrics'); } catch(e) { return; }
      const sig = 'single|' + JSON.stringify(data) + '|' + (document.documentElement.getAttribute('data-theme') || '');
      if (sig === metricsSig) return;
      metricsSig = sig;
      if (!data.epochs || !data.epochs.length) { showCharts(false, 'no data'); return; }
      const stitchNote = data.stitched_from ? ` · includes base ${shortRunName(String(data.stitched_from).split(/[\\/]/).pop().replace(/\.pt$/, ''))}` : '';
      showCharts(true, `${data.epochs.length} epoch(s)${stitchNote}`);
      const labels = data.epochs.map(String);
      const cPrimary = cssVar('--primary') || '#43d9bd';
      const cAccent = cssVar('--accent') || '#66a6ff';
      const cSuccess = cssVar('--success') || '#5ee48f';
      const cWarning = cssVar('--warning') || '#f5bd4f';
      drawCharts(labels,
        [ { label: 'train', data: data.train_loss, borderColor: cPrimary, backgroundColor: 'transparent', tension: 0.3, pointRadius: 3 },
          { label: 'val',   data: data.val_loss,   borderColor: cAccent,  backgroundColor: 'transparent', tension: 0.3, pointRadius: 3 } ],
        [ { label: 'train', data: data.train_acc, borderColor: cSuccess, backgroundColor: 'transparent', tension: 0.3, pointRadius: 3 },
          { label: 'val',   data: data.val_acc,   borderColor: cWarning, backgroundColor: 'transparent', tension: 0.3, pointRadius: 3 } ],
        { stage_boundary_index: data.stage_boundary_index, stage_boundary_label: data.stage_boundary_label });
    }

    async function loadMetricsCompare(names) {
      const qs = names && names.length ? ('?names=' + encodeURIComponent(names.join(','))) : '';
      let data;
      try { data = await api('/api/metrics/runs' + qs); } catch(e) { return; }
      const runs = (data.runs || []).filter(r => r.epochs && r.epochs.length);
      const sig = 'compare|' + JSON.stringify(runs) + '|' + (document.documentElement.getAttribute('data-theme') || '');
      if (sig === metricsSig) return;
      metricsSig = sig;
      if (!runs.length) { showCharts(false, 'no runs'); return; }
      showCharts(true, `${runs.length} run(s)`);
      let labels = [];
      runs.forEach(r => { if (r.epochs.length > labels.length) labels = r.epochs.map(String); });
      const ds = (key) => runs.map((r, i) => ({
        label: shortRunName(r.name), data: r[key],
        borderColor: RUN_PALETTE[i % RUN_PALETTE.length], backgroundColor: 'transparent',
        tension: 0.3, pointRadius: 2
      }));
      const meta = runs.length === 1
        ? { stage_boundary_index: runs[0].stage_boundary_index, stage_boundary_label: runs[0].stage_boundary_label }
        : {};
      drawCharts(labels, ds('val_loss'), ds('val_acc'), meta);
    }

    // Non-trainable categories (TurnMeta is disabled, GameEnd is a terminal marker) are not real
    // per-category decisions, so they never appear in the distribution charts even if a stale scan
    // still carries them.
    const DATASET_NON_TRAINABLE = ['TurnMeta', 'GameEnd'];
    function datasetCatChart(canvas, cats, prev) {
      if (prev) { try { prev.destroy(); } catch (e) {} }
      const bases = Object.keys(cats).filter(k => !k.includes(':') && !DATASET_NON_TRAINABLE.includes(k)).sort();
      if (!canvas || !bases.length) return null;
      const skip = bases.map(c => cats[c + ':skip'] || 0);
      const skipLegal = bases.map(c => cats[c + ':skip_legal'] || 0);   // passed despite a legal option
      const action = bases.map(c => Math.max(0, (cats[c] || 0) - (cats[c + ':skip'] || 0)));
      const forced = skip.map((s, i) => Math.max(0, s - skipLegal[i]));  // skip with no legal option
      const base = chartDefaults();
      const opts = { ...base, scales: { x: { ...base.scales.x, stacked: true }, y: { ...base.scales.y, stacked: true } } };
      return new Chart(canvas.getContext('2d'), {
        type: 'bar',
        data: { labels: bases, datasets: [
          { label: 'chosen action', data: action, backgroundColor: cssVar('--primary') || '#43d9bd' },
          { label: 'passed (legal option available)', data: skipLegal, backgroundColor: cssVar('--warning') || '#f5bd4f' },
          { label: 'skip - no legal option', data: forced, backgroundColor: cssVar('--magenta') || '#e36bff' }
        ] },
        options: opts
      });
    }

    let datasetChart = null, datasetSig = null, datasetSourceCharts = {};
    function renderDataset() {
      const cats = (lastStatus && lastStatus.categories) || {};
      const d = lastStatus || {};
      const sb = d.source_breakdown || {};
      // Skip the rebuild entirely when nothing relevant changed — the 5s poll
      // otherwise destroys+recreates the chart on every tick, causing flicker.
      const sig = JSON.stringify([cats, sb, d.usable_decisions, d.decision_records, d.decision_files,
        d.invalid_decisions, d.invalid_reasons, d.games_with_metadata, d.records_without_game_metadata,
        document.documentElement.getAttribute('data-theme')]);
      if (sig === datasetSig) return;
      datasetSig = sig;
      const NON_TRAINABLE = DATASET_NON_TRAINABLE;
      const bases = Object.keys(cats).filter(k => !k.includes(':') && !NON_TRAINABLE.includes(k)).sort();
      const empty = document.getElementById('dataset-empty');
      const canvas = document.getElementById('chart-dataset');
      const srcHost = document.getElementById('dataset-source-charts');
      renderDatasetHealth();
      // Tear down any existing per-source charts before re-rendering (or clearing).
      Object.values(datasetSourceCharts).forEach(c => { try { c.destroy(); } catch (e) {} });
      datasetSourceCharts = {};
      if (!bases.length) {
        if (empty) empty.style.display = '';
        if (canvas) canvas.style.display = 'none';
        if (srcHost) srcHost.innerHTML = '';
        if (datasetChart) { datasetChart.destroy(); datasetChart = null; }
        return;
      }
      if (empty) empty.style.display = 'none';
      if (canvas) canvas.style.display = '';
      datasetChart = datasetCatChart(canvas, cats, datasetChart);

      // Per-source charts (one per top-level source: benchmark / interactive / received / legacy).
      if (srcHost) {
        const order = ['benchmark', 'interactive', 'received', 'legacy'];
        const names = order.filter(s => sb[s]).concat(Object.keys(sb).filter(s => !order.includes(s)).sort());
        if (!names.length) {
          srcHost.innerHTML = '';
        } else {
          srcHost.innerHTML = names.map(s => {
            const usable = Object.entries(sb[s]).filter(([k]) => !k.includes(':') && !NON_TRAINABLE.includes(k))
              .reduce((a, [, v]) => a + v, 0);
            return `<div style="margin-top:14px"><div class="subtle" style="margin-bottom:4px">${escapeHtml(s)} <span class="mono">(${usable} usable)</span></div>`
              + `<div class="chart-wrap" style="height:200px"><canvas id="chart-src-${escapeHtml(s)}"></canvas></div></div>`;
          }).join('');
          names.forEach(s => {
            const cEl = document.getElementById('chart-src-' + s);
            const ch = datasetCatChart(cEl, sb[s], null);
            if (ch) datasetSourceCharts[s] = ch;
          });
        }
      }
      return;
    }

    function renderDatasetHealth() {
      const el = document.getElementById('dataset-health');
      if (!el) return;
      const d = lastStatus || {};
      if (d.usable_decisions == null) { el.innerHTML = '<span class="subtle">Scan the dataset to see health metrics.</span>'; return; }
      const invalid = d.invalid_decisions || 0;
      const cell = (k, v, cls) => `<div class="hcell"><div class="hk">${k}</div><div class="hv ${cls || ''}">${v}</div></div>`;
      let html = '';
      html += cell('usable', d.usable_decisions ?? '-');
      html += cell('records', d.decision_records ?? '-');
      html += cell('files', d.decision_files ?? '-');
      html += cell('invalid', invalid, invalid ? 'bad' : '');
      // Non-trainable records (TurnMeta/GameEnd) are intentionally excluded — not malformed.
      if (d.non_trainable_records) html += cell('non-trainable', d.non_trainable_records);
      // Game-metadata cells (games w/ metadata, records w/o metadata) intentionally hidden.
      const reasons = d.invalid_reasons || {};
      Object.keys(reasons).forEach(r => { html += cell(r, reasons[r], 'bad'); });
      el.innerHTML = html;
    }

    function applyTheme(theme) {
      const t = theme === 'light' ? 'light' : 'dark';
      document.documentElement.setAttribute('data-theme', t);
      try { localStorage.setItem('tcgml-theme', t); } catch (e) {}
      const btn = document.getElementById('theme-btn');
      if (btn) btn.textContent = t === 'light' ? 'Dark mode' : 'Light mode';
      // Invalidate cached signatures so charts rebuild with the new theme colors.
      metricsSig = null; datasetSig = null;
      renderDataset();
      if (lossChart || accChart) loadMetrics().catch(() => {});
    }

    function toggleTheme() {
      const current = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
      applyTheme(current === 'light' ? 'dark' : 'light');
    }

    function showTab(name) {
      document.querySelectorAll('.tab-panel').forEach(p => p.classList.toggle('active', p.dataset.tab === name));
      document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
      try { localStorage.setItem('tcgml-tab', name); } catch (e) {}
      if (name === 'training') { loadRunPicker().catch(() => {}); loadMetrics().catch(() => {}); refreshFtModelList().catch(() => {}); loadTrainSources().catch(() => {}); }
      if (name === 'environment') renderDataset();
      if (name === 'model') loadModels().catch(() => {});
      if (name === 'replay') loadReplayGames().catch(() => {});
      if (name === 'advisors') { loadAdvisorEvents().catch(() => {}); loadAdvisorModelPicker().catch(() => {}); }
      if (name === 'analysis' && !analysisLoaded) { loadAnalysis().catch(() => {}); }
    }

    // --- Replay ---
    let replay = { steps: [], idx: 0, diffOnly: false, hideTurnMeta: false, playerFilter: '', meta: {} };

    let replayGames = [];

    async function loadReplayGames() {
      const sel = document.getElementById('replay-game');
      const modelEl = document.getElementById('replay-model');
      try {
        const data = await api('/api/replay/games');
        modelEl.textContent = data.model_loaded ? '' : 'No model loaded yet — the latest one loads on first game open.';
        replayGames = data.games || [];
        replay.gameListTotal = data.total_count || replayGames.length;
        replay.gameListLimit = data.limit || replayGames.length;
        if (!replayGames.length) {
          sel.innerHTML = '<option value="">No decision logs found</option>';
          return;
        }
        // Populate the winner filter from the values actually present.
        const winners = [...new Set(replayGames.map(g => g.winner).filter(w => w != null))].sort();
        const wsel = document.getElementById('replay-winner');
        const prev = wsel.value;
        wsel.innerHTML = '<option value="">all</option>' + winners.map(w => `<option value="${escapeHtml(String(w))}">${escapeHtml(String(w))}</option>`).join('');
        wsel.value = prev;
        renderGameOptions();
      } catch (e) {
        sel.innerHTML = `<option value="">Error: ${escapeHtml(e.message)}</option>`;
      }
    }

    function renderGameOptions() {
      const sel = document.getElementById('replay-game');
      const wfilter = document.getElementById('replay-winner').value;
      const games = wfilter ? replayGames.filter(g => String(g.winner) === wfilter) : replayGames;
      const limitNote = replay.gameListTotal > replay.gameListLimit
        ? `latest ${games.length}/${replay.gameListTotal}`
        : `${games.length}`;
      sel.innerHTML = `<option value="">Select a game… (${limitNote})</option>` + games.map(g => {
        const win = g.winner != null ? ` · winner ${g.winner}` : '';
        const turns = g.turns != null ? ` · ${g.turns} turns` : '';
        const decks = g.deck_a || g.deck_b ? ` · ${g.deck_a || '?'} vs ${g.deck_b || '?'}` : '';
        const decisions = g.decisions != null && g.usable_decisions != null
          ? ` · ${g.usable_decisions}/${g.decisions} decisions`
          : ' · decision log';
        return `<option value="${escapeHtml(g.game_id)}">${escapeHtml(g.game_id)}${decks}${decisions}${turns}${win}</option>`;
      }).join('');
    }

    async function loadReplayGame(gameId) {
      const stage = document.getElementById('replay-stage');
      const summaryEl = document.getElementById('replay-summary');
      if (!gameId) { stage.style.display = 'none'; summaryEl.innerHTML = ''; return; }
      summaryEl.innerHTML = '<span class="subtle">Running model over every decision…</span>';
      let data;
      try { data = await api(`/api/replay/game/${encodeURIComponent(gameId)}`); }
      catch (e) { summaryEl.innerHTML = `<span class="hv bad">${escapeHtml(e.message)}</span>`; return; }

      // TurnMeta logging is disabled; drop any legacy TurnMeta rows from the replay entirely.
      data.steps = (data.steps || []).filter(st => st.category !== 'TurnMeta');

      const s = data.summary;
      // Exclude legacy TurnMeta from the headline counts too, so totals match the visible steps.
      const tm = s.by_category && s.by_category['TurnMeta'];
      const scored = s.scored - (tm ? tm.scored : 0);
      const agreements = s.agreements - (tm ? tm.agree : 0);
      const rate = scored ? ((agreements / scored) * 100).toFixed(1) + '%' : '—';
      const cats = Object.entries(s.by_category)
        .filter(([c]) => c !== 'TurnMeta')
        .map(([c, v]) => `<span class="chip">${escapeHtml(c)}: ${v.agree}/${v.scored}</span>`).join('');
      summaryEl.innerHTML =
        `<div class="replay-cards">` +
        `<div class="card"><div class="label">Match w/ expert</div><div class="value">${rate}</div></div>` +
        `<div class="card"><div class="label">Scored decisions</div><div class="value">${scored}</div></div>` +
        `<div class="card"><div class="label">Disagreements</div><div class="value">${scored - agreements}</div></div>` +
        `<div class="card"><div class="label">P1 deck · profile</div><div class="value mono">${escapeHtml(s.deck_a || '—')} · ${escapeHtml(s.profile_a || '—')}</div></div>` +
        `<div class="card"><div class="label">P2 deck · profile</div><div class="value mono">${escapeHtml(s.deck_b || '—')} · ${escapeHtml(s.profile_b || '—')}</div></div>` +
        `<div class="card"><div class="label">Model</div><div class="value mono">${escapeHtml((s.model_path || '—').split(/[\\/]/).pop())}</div></div>` +
        `</div><div class="chips">${cats}</div>`;

      let hideMeta = false;
      try { hideMeta = localStorage.getItem('tcgml-replay-hidemeta') === '1'; } catch (e) {}
      const playerFilter = document.getElementById('replay-player')?.value || '';
      replay = {
        steps: data.steps, idx: 0, diffOnly: false, hideTurnMeta: hideMeta, playerFilter,
        meta: { deck_a: s.deck_a, deck_b: s.deck_b, profile_a: s.profile_a, profile_b: s.profile_b },
      };
      document.getElementById('replay-diff-btn').classList.remove('active');
      const diffNote = document.getElementById('replay-diff-note');
      if (diffNote) diffNote.textContent = scored ? '' : 'No model loaded — "Only disagreements" needs a model (Model & Eval).';
      document.getElementById('replay-hidemeta-btn').classList.toggle('active', hideMeta);
      stage.style.display = '';
      const vis = visibleIndices();
      replay.idx = vis.length ? vis[0] : -1;
      const slider = document.getElementById('replay-slider');
      slider.max = Math.max(0, vis.length - 1);
      slider.value = 0;
      renderReplayStep();
    }

    function visibleIndices() {
      let idx = replay.steps.map((_, i) => i);
      if (replay.hideTurnMeta) {
        const filtered = idx.filter(i => replay.steps[i].category !== 'TurnMeta');
        if (filtered.length) idx = filtered;  // keep something to show if a game were all-meta
      }
      if (replay.diffOnly) {
        const only = idx.filter(i => replay.steps[i].agree === false);
        if (only.length) idx = only;
      }
      if (replay.playerFilter) {
        const only = idx.filter(i => String(replay.steps[i].player_id) === replay.playerFilter);
        idx = only;  // honor the filter even if it leaves nothing (e.g. a player made no logged decisions)
      }
      // Do not silently fall back to the other player's decisions. Human turns are not logged as
      // expert decisions, so a Human P1 legitimately produces an empty P1 replay filter.
      return replay.playerFilter ? idx : (idx.length ? idx : replay.steps.map((_, i) => i));
    }

    function setReplayPlayer(v) {
      replay.playerFilter = v;
      const vis = visibleIndices();
      if (!vis.includes(replay.idx)) replay.idx = vis.length ? vis[0] : -1;
      const slider = document.getElementById('replay-slider');
      slider.max = Math.max(0, vis.length - 1);
      slider.value = Math.max(0, vis.indexOf(replay.idx));
      renderReplayStep();
    }

    function toggleHideTurnMeta() {
      replay.hideTurnMeta = !replay.hideTurnMeta;
      try { localStorage.setItem('tcgml-replay-hidemeta', replay.hideTurnMeta ? '1' : '0'); } catch (e) {}
      document.getElementById('replay-hidemeta-btn').classList.toggle('active', replay.hideTurnMeta);
      const vis = visibleIndices();
      if (!vis.includes(replay.idx)) replay.idx = vis.length ? vis[0] : -1;
      const slider = document.getElementById('replay-slider');
      slider.value = vis.indexOf(replay.idx);
      slider.max = Math.max(0, vis.length - 1);
      renderReplayStep();
    }

    function toggleDiffOnly() {
      replay.diffOnly = !replay.diffOnly;
      document.getElementById('replay-diff-btn').classList.toggle('active', replay.diffOnly);
      // "Only disagreements" needs model scoring (step.agree). With no model loaded, every step has
      // agree=null, so the filter can't narrow anything — tell the user instead of silently no-op'ing.
      const note = document.getElementById('replay-diff-note');
      if (note) {
        const hasScored = replay.steps.some(s => s.agree === true || s.agree === false);
        note.textContent = (replay.diffOnly && !hasScored)
          ? 'Load a model (Model & Eval) to compute agreements — nothing to filter without one.'
          : '';
      }
      const vis = visibleIndices();
      if (!vis.includes(replay.idx)) replay.idx = vis.length ? vis[0] : -1;
      const slider = document.getElementById('replay-slider');
      slider.value = vis.indexOf(replay.idx);
      slider.max = Math.max(0, vis.length - 1);
      renderReplayStep();
    }

    function replayGoto(sliderPos) {
      const vis = visibleIndices();
      if (!vis.length) { replay.idx = -1; renderReplayStep(); return; }
      replay.idx = vis[Math.max(0, Math.min(sliderPos, vis.length - 1))];
      renderReplayStep();
    }

    function replayStep(delta) {
      const vis = visibleIndices();
      if (!vis.length) { replay.idx = -1; renderReplayStep(); return; }
      let pos = vis.indexOf(replay.idx) + delta;
      pos = Math.max(0, Math.min(pos, vis.length - 1));
      replay.idx = vis[pos];
      document.getElementById('replay-slider').value = pos;
      renderReplayStep();
    }

    function energySummary(energy) {
      const entries = Object.entries(energy || {}).filter(([, v]) => Number(v) > 0);
      return entries.length ? entries.map(([k, v]) => `${escapeHtml(k)} x${Number(v)}`).join(' ') : '<span class="subtle">no energy</span>';
    }

    function pokemonStatuses(p) {
      const statuses = [];
      if (p?.IsPoisoned) statuses.push('Poison');
      if (p?.IsBurned) statuses.push('Burn');
      if (p?.SpecialCondition && p.SpecialCondition !== 'None') statuses.push(p.SpecialCondition);
      if (p?.CanEvolve) statuses.push('Can evolve');
      return statuses;
    }

    function renderPokemonCard(p, active = false) {
      if (!p) return `<div class="bench-slot empty">${active ? 'No active' : 'Empty'}</div>`;
      const hp = Number(p.CurrentHp ?? 0);
      const maxHp = Math.max(1, Number(p.MaxHp ?? hp ?? 1));
      const pct = Math.max(0, Math.min(100, Math.round((hp / maxHp) * 100)));
      const hpClass = pct <= 35 ? ' danger' : '';
      const statuses = pokemonStatuses(p);
      const attacks = (p.Attacks || []).map(a => a?.Name).filter(Boolean).slice(0, 2).join(' / ');
      return `<div class="pokemon-card ${active ? 'active' : ''}">
        <div class="pokemon-name">${escapeHtml(p.Name || '?')}</div>
        <div class="pokemon-sub">${escapeHtml(p.Stage || '?')} · ${escapeHtml(p.PokemonType || '?')} · HP ${hp}/${maxHp}</div>
        <div class="hp-bar${hpClass}"><i style="width:${pct}%"></i></div>
        <div class="energy-pills">${energySummary(p.EnergyEquipped)}</div>
        ${statuses.length ? `<div class="status-pills">${statuses.map(s => `<span class="status-pill ${s === 'Can evolve' ? '' : 'bad'}">${escapeHtml(s)}</span>`).join('')}</div>` : ''}
        ${attacks ? `<div class="pokemon-sub">${escapeHtml(attacks)}</div>` : ''}
      </div>`;
    }

    function renderBench(bench, benchSize) {
      const size = Math.max(Number(benchSize || 0), (bench || []).length, 0);
      const columns = Math.max(1, Math.min(size || 1, 5));
      const slots = Array.from({ length: size }, (_, i) => (bench || [])[i] || null);
      return `<div class="bench-row" style="grid-template-columns: repeat(${columns}, minmax(0, 1fr));">${slots.map(p => p ? renderPokemonCard(p, false) : '<div class="bench-slot empty">Empty bench</div>').join('')}</div>`;
    }

    function renderBoardPlayer(label, state, acting) {
      const s = state || {};
      const energy = s.AvailableEnergy && s.AvailableEnergy !== 'None'
        ? escapeHtml(s.AvailableEnergy)
        : '<span class="subtle">none</span>';
      const next = s.NextEnergy && s.NextEnergy !== 'None'
        ? ` · next ${escapeHtml(s.NextEnergy)}`
        : '';
      return `<div class="board-player">
        <div class="board-side-meta">
          <div class="board-side-title ${acting ? 'acting' : ''}">${escapeHtml(label)}${acting ? ' · acting' : ''}</div>
          <div class="board-kv">score ${Number(s.Score || 0)} · deck ${Number(s.DeckCount || 0)} · discard ${Number(s.DiscardCount || 0)}</div>
          <div class="board-kv">energy zone ${energy}${next}</div>
          <div class="board-kv">${s.CanAddEnergy ? 'can attach energy' : 'energy attach unavailable'} · ${s.UsedSupporterThisTurn ? 'supporter used' : 'supporter open'}</div>
        </div>
        ${renderPokemonCard(s.ActivePokemon, true)}
        ${renderBench(s.Bench, s.BenchSize)}
      </div>`;
    }

    function renderBoardState(step) {
      const snap = step.snapshot || {};
      const my = snap.MyState || {};
      const opp = snap.OpponentState || {};
      return `<div class="board-state">
        <h3>Board state before expert action</h3>
        <div class="board-grid">
          ${renderBoardPlayer(`P${opp.PlayerId || '?'}`, opp, snap.ActivePlayerId === opp.PlayerId)}
          ${renderBoardPlayer(`P${my.PlayerId || '?'}`, my, snap.ActivePlayerId === my.PlayerId)}
        </div>
      </div>`;
    }

    function renderReplayStep() {
      const el = document.getElementById('replay-step');
      const step = replay.steps[replay.idx];
      if (!step) { el.innerHTML = '<span class="subtle">No step.</span>'; return; }
      const vis = visibleIndices();
      const pos = vis.indexOf(replay.idx) + 1;
      document.getElementById('replay-slider').max = Math.max(0, vis.length - 1);

      let verdict = '<span class="chip">not scored</span>';
      if (step.agree === true && step.exact_agree === false) verdict = '<span class="chip ok">✓ equivalent choice</span>';
      else if (step.agree === true) verdict = '<span class="chip ok">✓ model agrees</span>';
      else if (step.agree === false) verdict = '<span class="chip bad">✗ disagreement</span>';

      const rows = (step.candidates || []).map(c => {
        const prob = c.model_prob != null ? (c.model_prob * 100).toFixed(1) + '%' : '—';
        const marks = [c.is_expert ? '<span class="tag expert">expert</span>' : '',
                       c.is_model ? '<span class="tag model">model</span>' : ''].join('');
        const reasons = (c.reasons || []).join(' · ');
        const displayLabel = c.display_label || c.label;
        return `<tr class="${c.blocked ? 'blocked' : ''} ${c.is_expert ? 'is-expert' : ''} ${c.is_model ? 'is-model' : ''}">
          <td>${marks}</td>
          <td><span class="mono">${escapeHtml(displayLabel)}</span>${c.blocked ? ' <span class="subtle">(strategically blocked)</span>' : ''}</td>
          <td>${c.expert_score}</td>
          <td>${prob}</td>
          <td class="subtle">${escapeHtml(reasons)}</td>
        </tr>`;
      }).join('');

      const m = replay.meta || {};
      const actProfile = step.player_id === 1 ? m.profile_a : (step.player_id === 2 ? m.profile_b : null);
      const actDeck = step.player_id === 1 ? m.deck_a : (step.player_id === 2 ? m.deck_b : null);
      const playerChip = `P${step.player_id}` +
        (actDeck ? ` · ${escapeHtml(actDeck)}` : '') +
        (actProfile ? ` · ${escapeHtml(actProfile)}` : '');
      el.innerHTML =
        `<div class="replay-head">
           <span class="chip">seq ${step.seq}</span>
           <span class="chip">turn ${step.turn}</span>
           <span class="chip" title="Acting player · deck · active weight profile">${playerChip}</span>
           <span class="chip">${escapeHtml(step.category)}</span>
           ${verdict}
           <span class="subtle" style="margin-left:auto">${pos} / ${vis.length}</span>
         </div>
         ${step.usable ? '' : `<p class="subtle">Not scored: ${escapeHtml(step.reason)}.</p>`}
         <table class="replay-table">
           <thead><tr><th></th><th>Candidate</th><th>Expert score</th><th>Model prob</th><th><span class="reason-help" id="expert-reason-help">Expert reasons <button type="button" class="info-dot" id="expert-reason-info" aria-label="Explain expert reasons" aria-expanded="false" onclick="toggleExpertReasonHelp(event)">i</button></span></th></tr></thead>
           <tbody>${rows}</tbody>
         </table>
         ${renderBoardState(step)}`;
    }

    (function initUi() {
      let savedTheme = 'dark', savedTab = 'environment';
      try {
        savedTheme = localStorage.getItem('tcgml-theme') || 'dark';
        savedTab = localStorage.getItem('tcgml-tab') || 'environment';
      } catch (e) {}
      applyTheme(savedTheme);
      restoreTrainForm();
      updateGamesHint();
      let dockLog = false;
      try { dockLog = localStorage.getItem('tcgml-dock-log') === '1'; } catch (e) {}
      applyDockLog(dockLog);
      if (document.querySelector(`.tab-btn[data-tab="${savedTab}"]`)) showTab(savedTab);
    })();

    refresh().then(() => { loadAdvisorModelPicker().catch(() => {}); refreshFtModelList().catch(() => {}); loadTrainSources().catch(() => {}); }).catch(() => {});
    loadPaths();
    loadMetrics();
    loadAdvisorEvents();
    loadAnalysis().catch(() => {});
    setInterval(() => {
      refresh();
      if (lastStatus.setup === 'running') loadLog('setup').catch(() => {});
      if (lastStatus.training === 'running') { loadLog('train').catch(() => {}); loadRunPicker().catch(() => {}); loadMetrics().catch(() => {}); }
      if (lastStatus.evaluation === 'running') loadLog('eval').catch(() => {});
      if (lastStatus.log_sync === 'running') loadLog('sync').catch(() => {});
      loadAdvisorEvents().catch(() => {});
    }, 5000);
  </script>
  <div class="modal-backdrop" id="json-patch-backup-modal" role="dialog" aria-modal="true" aria-labelledby="json-patch-backup-title">
    <div class="modal-card">
      <h2 id="json-patch-backup-title">Archive training logs?</h2>
      <p class="subtle" style="margin:0; line-height:1.5">
        Download patch will always archive the current <span class="mono">Cards/</span> and <span class="mono">Decks/</span> folders before mirroring new JSON files.
        Training logs can also be archived so previous model data remains tied to the old card/deck meta.
      </p>
      <p class="subtle" style="margin:10px 0 0; line-height:1.5">
        Include <span class="mono">Logs Export/ML/Decisions</span> and <span class="mono">Logs Export/Deckbuilder</span> in this backup?
      </p>
      <div class="modal-actions">
        <button class="secondary" type="button" onclick="resolveJsonPatchBackupChoice(false)">Skip log backup</button>
        <button class="primary" type="button" onclick="resolveJsonPatchBackupChoice(true)">Archive logs and continue</button>
      </div>
    </div>
  </div>
</body>
</html>"""


def create_app(
    root: Path | None = None,
    *,
    ml_root: Path | None = None,
    build_root: Path | None = None,
    logs_dir: Path | None = None,
    cards_dir: Path | None = None,
    decks_dir: Path | None = None,
):
    from fastapi import FastAPI, HTTPException
    from fastapi.responses import FileResponse, HTMLResponse

    # Startup values from CLI/env are the "default". The dashboard can override the
    # data paths live (e.g. point at a different build's "Logs Export/ML"); overrides
    # are persisted to ui_paths.json next to the pipeline so they survive restarts.
    startup_paths = {
        "root": root, "ml_root": ml_root, "build_root": build_root, "logs_dir": logs_dir,
        "cards_dir": cards_dir, "decks_dir": decks_dir,
    }

    def _resolve(overrides: dict[str, Any]):
        return get_paths(
            overrides.get("root") or startup_paths["root"],
            ml_root=overrides.get("ml_root") or startup_paths["ml_root"],
            build_root=overrides.get("build_root") or startup_paths["build_root"],
            logs_dir=overrides.get("logs_dir") or startup_paths["logs_dir"],
            cards_dir=overrides.get("cards_dir") or startup_paths["cards_dir"],
            decks_dir=overrides.get("decks_dir") or startup_paths["decks_dir"],
        )

    paths = _resolve({})
    ui_paths_file = paths.python_dir / "ui_paths.json"
    ui_overrides: dict[str, str] = {}
    if ui_paths_file.exists():
        try:
            ui_overrides = {k: str(v) for k, v in json.loads(ui_paths_file.read_text()).items() if v}
        except Exception:
            ui_overrides = {}
    if ui_overrides:
        paths = _resolve(ui_overrides)
    paths.runs_dir.mkdir(parents=True, exist_ok=True)
    paths.models_dir.mkdir(parents=True, exist_ok=True)
    spec = load_spec(paths.feature_spec)
    catalog = CardCatalog.load(paths.cards_dir)
    encoder = FeatureEncoder(spec=spec, catalog=catalog)
    loaded_model: dict[str, Any] = {"path": None, "module": None, "device": None}
    processes: dict[str, subprocess.Popen | None] = {"setup": None, "train": None, "eval": None}
    logs: dict[str, Path | None] = {"setup": None, "train": None, "eval": None, "sync": None}
    # Log-sync state: pulls decision logs collected from other people's builds off the SMB
    # share into the local Decisions folder. Runs in a daemon thread; the UI polls the "sync" log.
    sync_state: dict[str, Any] = {"running": False, "summary": None}
    # Curriculum state: tracks the two-stage pre-train → fine-tune pipeline.
    # stage=0 means idle; stage=1/2 means that stage is active or just finished.
    curriculum_state: dict[str, Any] = {
        "active": False, "stage": 0, "stage1_model": None, "cancelled": False,
    }
    advisor_events: list[dict[str, Any]] = []
    card_usage_cache: dict[str, Any] = {"stamp": None, "data": None}
    # Per-decision-file partial aggregates for incremental card-usage. Decision files are immutable
    # once a battle ends, so a file whose (size, mtime_ns) is unchanged keeps its cached aggregate;
    # only new/changed files are rescanned. Keyed by str(path). Persisted to disk (lazily loaded on
    # first use) so a server restart does not force a re-read of the whole multi-GB dataset.
    card_usage_file_cache: dict[str, dict[str, Any]] = {}
    card_usage_disk = {"loaded": False}
    # Cached /api/analysis results keyed by games.jsonl stamp + (source, matchup). The parsed games
    # list is reused across filters within the same stamp; by_filter holds per-filter analyze_games
    # results. card_usage is added on read (it has its own stamp-based cache).
    analysis_cache: dict[str, Any] = {"stamp": None, "games": None, "by_filter": {}}
    replay_index_cache: dict[str, Any] = {"stamp": None, "data": None}
    decision_file_cache: dict[str, Any] = {"updated_monotonic": 0.0, "data": None}
    dataset_cache: dict[str, Any] = {
        "decision_files": len(iter_decision_files(paths.decisions_dir)),
        "games_with_metadata": None,
        "decision_records": None,
        "usable_decisions": None,
        "invalid_decisions": None,
        "invalid_reasons": {},
        "categories": {},
        "records_without_game_metadata": None,
        "last_scan_unix_ms": None,
    }
    app = FastAPI(title="TCG Station ML")

    def is_running(kind: str) -> bool:
        process = processes.get(kind)
        return process is not None and process.poll() is None

    def process_status(kind: str) -> str:
        process = processes.get(kind)
        if process is None:
            return "stopped"
        code = process.poll()
        return "running" if code is None else f"exited({code})"

    # Windows needs a separate process group so we can deliver CTRL_BREAK to a child
    # without also signalling the dashboard itself.
    new_group_kwargs: dict[str, Any] = (
        {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {}
    )

    def stop_process(kind: str, graceful: bool = False) -> dict[str, Any]:
        process = processes.get(kind)
        if process is None:
            return {"stopped": True, "status": "not-started"}
        if process.poll() is None:
            interrupted = False
            if graceful:
                # Ask the child to stop and save (it installs a handler for this).
                # On Windows the child must be in its own process group (see new_group_kwargs).
                try:
                    sig = signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGINT
                    process.send_signal(sig)
                    interrupted = True
                except Exception:
                    interrupted = False
            if interrupted:
                try:
                    process.wait(timeout=20)  # allow time to checkpoint and exit cleanly
                except subprocess.TimeoutExpired:
                    pass
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    process.kill()
        return {"stopped": True, "status": process_status(kind)}

    def open_log(kind: str):
        log_path = paths.runs_dir / f"{kind}_{time.strftime('%Y%m%d_%H%M%S')}.log"
        logs[kind] = log_path
        return log_path.open("w", encoding="utf-8", buffering=1)

    def tail(path: Path | None, max_bytes: int = 24000) -> str:
        if path is None or not path.exists():
            return ""
        size = path.stat().st_size
        with path.open("rb") as handle:
            if size > max_bytes:
                handle.seek(size - max_bytes)
            data = handle.read()
        return data.decode("utf-8", errors="replace")

    def model_trained_unix_ms(path: Path, meta: dict[str, Any] | None = None) -> int:
        run_name = str(meta.get("run") if isinstance(meta, dict) and meta.get("run") else path.stem)
        match = re.search(r"(\d{8})_(\d{6})", run_name)
        if not match:
            return int(path.stat().st_mtime * 1000)
        stamp = f"{match.group(1)}{match.group(2)}"
        try:
            parsed = time.strptime(stamp, "%Y%m%d%H%M%S")
        except ValueError:
            return int(path.stat().st_mtime * 1000)
        return int(time.mktime(parsed) * 1000)

    def latest_model_path() -> Path | None:
        models = sorted(paths.models_dir.glob("*.pt"), key=model_trained_unix_ms, reverse=True)
        return models[0] if models else None

    def package_available(name: str) -> bool:
        return importlib.util.find_spec(name) is not None

    def environment_status() -> dict[str, Any]:
        info: dict[str, Any] = {
            "python": sys.executable,
            "packages": {
                "numpy": package_available("numpy"),
                "torch": package_available("torch"),
                "fastapi": package_available("fastapi"),
            },
            "cuda_available": False,
            "cuda_device": None,
            "auto_device": "cpu",
            "devices": {},
        }
        if info["packages"]["torch"]:
            try:
                import torch

                info["torch_version"] = torch.__version__
                device_report = torch_device_report()
                info["auto_device"] = device_report.get("selected_auto_device")
                info["devices"] = device_report.get("devices", {})
                cuda = info["devices"].get("cuda", {}) if isinstance(info["devices"], dict) else {}
                info["cuda_available"] = bool(cuda.get("available"))
                info["cuda_device"] = cuda.get("device_name")
            except Exception as exc:
                info["torch_error"] = str(exc)
        return info

    def _normalize_image_stem(stem: str) -> str:
        # Make matching tolerant of the small drifts between the card JSON's
        # imageName and the file actually on disk: spaces vs underscores
        # ("professor oak_1" -> "professor_oak_1") and zero-padded indices
        # ("potion_01" -> "potion_1").
        stem = re.sub(r"[ _]+", "_", stem.lower())
        return re.sub(r"_0*(\d+)$", r"_\1", stem)

    _image_exts = {".png", ".jpg", ".jpeg", ".webp"}

    def card_image_path(card: dict[str, Any] | None) -> Path | None:
        if not isinstance(card, dict):
            return None
        image_name = str(card.get("imageName") or "").strip()
        if not image_name:
            return None
        resources = paths.root / "Assets" / "Resources"
        if not resources.exists():
            return None
        bases = [resources / "Pokemon_Images", resources / "Trainer_images"]
        # Fast path: exact filename match.
        for base in bases:
            if not base.exists():
                continue
            matches = list(base.rglob(image_name))
            if matches:
                return matches[0]
        # Fallback: many cards point at "<name>.png" while the graphic on disk
        # is a ".jpg" (or differs only in spacing / zero-padding). Match on the
        # normalized stem, ignoring the extension, so those images still show.
        target = _normalize_image_stem(Path(image_name).stem)
        for base in bases:
            if not base.exists():
                continue
            for candidate in base.rglob("*"):
                if (
                    candidate.suffix.lower() in _image_exts
                    and _normalize_image_stem(candidate.stem) == target
                ):
                    return candidate
        return None

    def card_image_url(name: str | None) -> str | None:
        card = catalog.get_by_name(name)
        return f"/api/card-image/{quote(str(name))}" if card_image_path(card) else None

    def effect_labels(effects: Any) -> list[str]:
        labels: list[str] = []
        if not isinstance(effects, list):
            return labels
        for effect in effects[:4]:
            if not isinstance(effect, dict):
                continue
            typ = str(effect.get("cardEffectType") or "").strip()
            target = str(effect.get("cardEffectTarget") or "").strip()
            amount = effect.get("effectAmount")
            pieces = [p for p in (typ, target) if p and p != "None"]
            if amount not in (None, "", 0):
                pieces.append(str(amount))
            if pieces:
                labels.append(" ".join(pieces))
        return labels

    def describe_card(name: str | None) -> dict[str, Any]:
        card = catalog.get_by_name(name)
        if not isinstance(card, dict):
            return {
                "name": name or "Unknown",
                "type": "Unknown",
                "subtitle": "No card JSON found",
                "description": "",
                "effects": [],
                "attacks": [],
                "image_url": None,
            }
        card_type = str(card.get("cardType") or "Card")
        subtype = str(card.get("trainerSubType") or "").strip()
        pokemon_type = str(card.get("type") or "").strip()
        stage = card.get("stage")
        subtitle_parts = [card_type]
        if subtype and subtype != "None":
            subtitle_parts.append(subtype)
        if pokemon_type:
            subtitle_parts.append(pokemon_type)
        if stage not in (None, "", "None"):
            subtitle_parts.append(f"Stage {stage}")

        attacks: list[dict[str, Any]] = []
        for attack in (card.get("attacks") if isinstance(card.get("attacks"), list) else []):
            if not isinstance(attack, dict):
                continue
            attacks.append({
                "name": attack.get("attackName") or attack.get("Name") or "Attack",
                "damage": attack.get("damage") if attack.get("damage") is not None else attack.get("Damage"),
                "cost": attack.get("attackCost") or attack.get("EnergyCost") or [],
                "description": attack.get("attackDescription") or "",
                "effects": effect_labels(attack.get("effects")),
            })

        description = str(card.get("effectDescription") or "").strip()
        if not description and attacks:
            description = str(attacks[0].get("description") or "").strip()

        return {
            "name": card.get("cardName") or name or "Unknown",
            "type": card_type,
            "subtitle": " · ".join(subtitle_parts),
            "description": description,
            "effects": effect_labels(card.get("effects")),
            "attacks": attacks[:3],
            "image_url": card_image_url(str(card.get("cardName") or name)),
        }

    def label_card_name(category: str, label: str, snapshot: dict[str, Any]) -> tuple[str | None, str]:
        if category == "Attack":
            active = ((snapshot.get("MyState") or {}).get("ActivePokemon") or {})
            return active.get("Name"), "attack"
        if category == "PlayBasic":
            match = re.search(r"PlayBasic\((.*?)\)", label)
            return (match.group(1).strip() if match else None), "play"
        if category == "Evolve":
            match = re.search(r"Evolve\((.*?)\s+onto\s+", label)
            return (match.group(1).strip() if match else None), "play"
        if category == "PlayTrainer":
            match = re.search(r"PlayTrainer\((.*?)(?:\s+\[|,|\))", label)
            return (match.group(1).strip() if match else None), "play"
        return None, ""

    def line_json_field(line: str, key: str) -> str | None:
        token = f'"{key}":'
        idx = line.find(token)
        if idx >= 0:
            pos = idx + len(token)
            while pos < len(line) and line[pos].isspace():
                pos += 1
            if pos < len(line) and line[pos] == '"':
                end = line.find('"', pos + 1)
                return line[pos + 1:end] if end >= 0 else None
            end = pos
            while end < len(line) and line[end] not in ",}]\r\n":
                end += 1
            value = line[pos:end].strip()
            return value or None
        match = re.search(rf'"{re.escape(key)}"\s*:\s*(?:"([^"]*)"|(-?\d+(?:\.\d+)?))', line)
        if not match:
            return None
        return match.group(1) if match.group(1) is not None else match.group(2)

    def active_name_from_line(line: str) -> str | None:
        start = line.find('"MyState"')
        active = line.find('"ActivePokemon"', start if start >= 0 else 0)
        name = line.find('"Name":"', active if active >= 0 else 0)
        if name >= 0:
            pos = name + len('"Name":"')
            end = line.find('"', pos)
            if end >= 0:
                return line[pos:end]
        match = re.search(r'"MyState"\s*:\s*\{.*?"ActivePokemon"\s*:\s*\{.*?"Name"\s*:\s*"([^"]+)"', line)
        return match.group(1) if match else None

    def opponent_active_name_from_line(line: str) -> str | None:
        start = line.find('"OpponentState"')
        active = line.find('"ActivePokemon"', start if start >= 0 else 0)
        name = line.find('"Name":"', active if active >= 0 else 0)
        if name >= 0:
            pos = name + len('"Name":"')
            end = line.find('"', pos)
            if end >= 0:
                return line[pos:end]
        match = re.search(r'"OpponentState"\s*:\s*\{.*?"ActivePokemon"\s*:\s*\{.*?"Name"\s*:\s*"([^"]+)"', line)
        return match.group(1) if match else None

    def active_energy_total_from_line(line: str, state_key: str) -> int:
        start = line.find(f'"{state_key}"')
        active = line.find('"ActivePokemon"', start if start >= 0 else 0)
        energy = line.find('"EnergyEquipped":{', active if active >= 0 else 0)
        if energy < 0:
            return 0
        pos = energy + len('"EnergyEquipped":{')
        end = line.find("}", pos)
        if end < 0:
            return 0
        return sum(int(n) for n in re.findall(r':(-?\d+)', line[pos:end]))

    def attack_effect_summary(attack: dict[str, Any]) -> tuple[int, int, int, list[str]]:
        effects = attack.get("effects") if isinstance(attack.get("effects"), list) else []
        hit_count = 1
        self_discard = 0
        effect_names: list[str] = []
        for effect in effects:
            if not isinstance(effect, dict):
                continue
            typ = str(effect.get("cardEffectType") or "")
            amount = effect.get("effectAmount")
            target = str(effect.get("cardEffectTarget") or "")
            if typ:
                effect_names.append(typ)
            if typ == "Multiattack" and isinstance(amount, (int, float)):
                hit_count = max(hit_count, int(amount), 1)
            elif typ == "EnergyDiscard" and target == "Self" and isinstance(amount, (int, float)):
                self_discard += max(0, int(amount))
        return hit_count, self_discard, len(effects), effect_names[:5]

    def attack_damage_from_line(line: str, label: str, card_name: str | None) -> dict[str, Any] | None:
        card = catalog.get_by_name(card_name)
        if not isinstance(card, dict):
            return None
        attacks = card.get("attacks") if isinstance(card.get("attacks"), list) else []
        if not attacks:
            return None
        idx_match = re.search(r"Attack\[(\d+)\]\s*(.*)", label)
        idx = int(idx_match.group(1)) if idx_match else 0
        label_attack_name = (idx_match.group(2).strip() if idx_match else "").strip()
        attack = attacks[idx] if 0 <= idx < len(attacks) and isinstance(attacks[idx], dict) else None
        if attack is None and label_attack_name:
            attack = next((a for a in attacks
                           if isinstance(a, dict) and str(a.get("attackName") or "").strip() == label_attack_name), None)
        if not isinstance(attack, dict):
            return None
        base_damage = attack.get("damage")
        if not isinstance(base_damage, (int, float)):
            return None
        hit_count, self_discard, effect_count, effect_names = attack_effect_summary(attack)
        attacker_energy = active_energy_total_from_line(line, "MyState")
        defender_energy = active_energy_total_from_line(line, "OpponentState")
        per_hit_damage = float(base_damage)
        effects = attack.get("effects") if isinstance(attack.get("effects"), list) else []
        for effect in effects:
            if not isinstance(effect, dict):
                continue
            typ = str(effect.get("cardEffectType") or "")
            amount = effect.get("effectAmount")
            if not isinstance(amount, (int, float)):
                continue
            if typ == "PowerUp":
                per_hit_damage += float(amount) * max(0, attacker_energy - self_discard)
            elif typ == "Psychic":
                per_hit_damage += float(amount) * defender_energy
        per_hit_damage = max(0.0, per_hit_damage)
        total_damage = per_hit_damage * hit_count
        return {
            "damage": total_damage,
            "base_damage": float(base_damage),
            "per_hit_damage": per_hit_damage,
            "hit_count": hit_count,
            "effect_count": effect_count,
            "effect_names": effect_names,
            "attacker_energy": attacker_energy,
            "defender_energy": defender_energy,
            "card": card_name or card.get("cardName") or "Unknown",
            "attack": attack.get("attackName") or label_attack_name or label,
            "turn": line_json_field(line, "turn"),
            "player_id": line_json_field(line, "player_id"),
            "game_id": line_json_field(line, "game_id"),
            "target": opponent_active_name_from_line(line) or "opponent active",
        }

    DECISION_FILE_INDEX_TTL_SECONDS = 30.0

    def invalidate_decision_file_cache() -> None:
        decision_file_cache.update({"updated_monotonic": 0.0, "data": None})

    def decision_file_index(force: bool = False) -> dict[str, Any]:
        now = time.monotonic()
        cached = decision_file_cache.get("data")
        if (
            not force
            and cached is not None
            and now - float(decision_file_cache.get("updated_monotonic") or 0.0) < DECISION_FILE_INDEX_TTL_SECONDS
        ):
            return cached

        files: list[Path] = []
        sources: dict[str, int] = {}
        total_size = 0
        max_mtime = 0
        # Resolve the root once; file_source() resolves per call, which costs tens of seconds
        # over 50k+ files on Windows.
        root = paths.decisions_dir.resolve() if paths.decisions_dir.exists() else None
        candidates = root.rglob("*_decisions.jsonl") if root is not None else []
        for path in candidates:
            try:
                st = path.stat()
            except OSError:
                continue
            if not path.is_file() or st.st_size <= 3:
                continue
            files.append(path)
            total_size += st.st_size
            max_mtime = max(max_mtime, st.st_mtime_ns)
            parts = path.relative_to(root).parts[:-1]
            src = "/".join(parts) if parts else ROOT_SOURCE
            sources[src] = sources.get(src, 0) + 1
        files = sorted(files)
        data = {
            "files": files,
            "sources": sources,
            "stamp": (len(files), total_size, max_mtime),
        }
        decision_file_cache.update({"updated_monotonic": now, "data": data})
        return data

    def decision_logs_stamp() -> tuple[int, int, int]:
        return decision_file_index()["stamp"]

    def aggregate_card_usage_file(path: Path) -> dict[str, Any]:
        """Card-usage counters for a single decision file. Returned dict is mergeable: all counters
        are additive and biggest_hit combines by max damage. Cached per file so an unchanged file is
        never re-read."""
        usage: dict[str, int] = {}
        played: dict[str, int] = {}
        attack_sources: dict[str, int] = {}
        category_counts: dict[str, int] = {}
        biggest_hit: dict[str, Any] | None = None
        total_records = 0
        usable_records = 0
        try:
            handle = path.open("r", encoding="utf-8-sig")
        except OSError:
            handle = None
        if handle is not None:
            with handle:
                for line in handle:
                    if not line.strip():
                        continue
                    total_records += 1
                    category = line_json_field(line, "category") or "Unknown"
                    label = line_json_field(line, "chosen_label") or ""
                    if not label or label == "(skip)" or category == "GameEnd":
                        continue
                    card_name = None
                    usage_kind = ""
                    if category == "Attack":
                        card_name = active_name_from_line(line)
                        usage_kind = "attack"
                    else:
                        card_name, usage_kind = label_card_name(category, label, {})
                    if card_name:
                        usable_records += 1
                        usage[card_name] = usage.get(card_name, 0) + 1
                        category_counts[category] = category_counts.get(category, 0) + 1
                        if usage_kind == "play":
                            played[card_name] = played.get(card_name, 0) + 1
                        elif usage_kind == "attack":
                            attack_sources[card_name] = attack_sources.get(card_name, 0) + 1

                    if category == "Attack":
                        hit = attack_damage_from_line(line, label, card_name)
                        if hit and (biggest_hit is None or hit["damage"] > biggest_hit["damage"]):
                            biggest_hit = hit
        return {
            "usage": usage,
            "played": played,
            "attack_sources": attack_sources,
            "category_counts": category_counts,
            "biggest_hit": biggest_hit,
            "total_records": total_records,
            "usable_records": usable_records,
        }

    def card_usage_cache_path() -> Path:
        return paths.runs_dir / "card_usage_cache.json"

    def load_card_usage_file_cache() -> None:
        if card_usage_disk["loaded"]:
            return
        card_usage_disk["loaded"] = True
        try:
            with card_usage_cache_path().open("r", encoding="utf-8") as handle:
                data = json.load(handle)
        except (OSError, json.JSONDecodeError):
            return
        files = data.get("files") if isinstance(data, dict) and data.get("version") == 1 else None
        if isinstance(files, dict):
            card_usage_file_cache.update(files)

    def save_card_usage_file_cache() -> None:
        target = card_usage_cache_path()
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            tmp = target.with_suffix(target.suffix + ".tmp")
            with tmp.open("w", encoding="utf-8") as handle:
                json.dump({"version": 1, "files": card_usage_file_cache}, handle, separators=(",", ":"))
            os.replace(tmp, target)
        except OSError:
            return  # the disk cache is an optimization; a failed save must never break the scan

    def analyze_card_usage() -> dict[str, Any]:
        stamp = decision_logs_stamp()
        if card_usage_cache.get("stamp") == stamp and card_usage_cache.get("data") is not None:
            return card_usage_cache["data"]
        load_card_usage_file_cache()

        usage: dict[str, int] = {}
        played: dict[str, int] = {}
        attack_sources: dict[str, int] = {}
        category_counts: dict[str, int] = {}
        biggest_hit: dict[str, Any] | None = None
        total_records = 0
        usable_records = 0

        def merge_counter(dst: dict[str, int], src: dict[str, int]) -> None:
            for k, v in src.items():
                dst[k] = dst.get(k, 0) + v

        # Incremental scan: reuse cached per-file aggregates for files whose (size, mtime) is
        # unchanged, recompute only new/changed files, and drop cache entries for files that no
        # longer exist (so deletions shrink the totals and free memory).
        seen_keys: set[str] = set()
        recomputed = 0
        for path in iter_decision_files(paths.decisions_dir):
            key = str(path)
            seen_keys.add(key)
            try:
                st = path.stat()
                file_stamp = [st.st_size, st.st_mtime_ns]
            except OSError:
                file_stamp = None
            cached = card_usage_file_cache.get(key)
            if cached is not None and file_stamp is not None and list(cached.get("stamp") or ()) == file_stamp:
                agg = cached["agg"]
            else:
                agg = aggregate_card_usage_file(path)
                recomputed += 1
                if file_stamp is not None:
                    card_usage_file_cache[key] = {"stamp": file_stamp, "agg": agg}
            merge_counter(usage, agg["usage"])
            merge_counter(played, agg["played"])
            merge_counter(attack_sources, agg["attack_sources"])
            merge_counter(category_counts, agg["category_counts"])
            total_records += agg["total_records"]
            usable_records += agg["usable_records"]
            hit = agg["biggest_hit"]
            if hit and (biggest_hit is None or hit["damage"] > biggest_hit["damage"]):
                biggest_hit = hit

        stale_keys = [k for k in card_usage_file_cache if k not in seen_keys]
        for stale in stale_keys:
            del card_usage_file_cache[stale]
        if recomputed or stale_keys:
            save_card_usage_file_cache()

        # biggest_hit is reused across merges; copy before annotating so the cached per-file dict
        # stays free of the derived card_info field.
        if biggest_hit is not None:
            biggest_hit = dict(biggest_hit)

        def ranked(counter: dict[str, int], reverse: bool = True, limit: int = 8) -> list[dict[str, Any]]:
            items = sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]) if reverse else (kv[1], kv[0]))
            return [{"count": count, "card": describe_card(name)} for name, count in items[:limit]]

        most_used = ranked(usage, True, 1)
        most_played = ranked(played, True, 1)
        least_used = ranked(usage, False, 1)
        top_cards = ranked(usage, True, 8)
        top_attackers = ranked(attack_sources, True, 6)

        if biggest_hit is not None:
            biggest_hit["card_info"] = describe_card(str(biggest_hit.get("card") or ""))

        result = {
            "decision_records": total_records,
            "card_usage_records": usable_records,
            "cards_seen": len(usage),
            "category_counts": category_counts,
            "most_used": most_used[0] if most_used else None,
            "most_played": most_played[0] if most_played else None,
            "least_used": least_used[0] if least_used else None,
            "top_cards": top_cards,
            "top_attackers": top_attackers,
            "biggest_hit": biggest_hit,
            "notes": [
                "Usage is computed from decision logs. Attack usage is attributed to the active Pokemon in the pre-action snapshot.",
                "Attack damage estimates include PowerUp/Psychic scaling and Multiattack hit counts from card JSON, but do not replay every post-action combat event.",
            ],
        }
        card_usage_cache.update({"stamp": stamp, "data": result})
        return result

    def load_model(path: Path | None = None, device: str = "auto"):
        try:
            import torch
        except ModuleNotFoundError as exc:
            raise RuntimeError(
                "PyTorch is not installed in this dashboard environment. "
                "Run Environment -> Install / repair dependencies, or restart with "
                "start_dashboard_WINDOWS.cmd so requirements.txt is installed."
            ) from exc

        model_path = path or latest_model_path()
        if model_path is None:
            raise FileNotFoundError("No .pt model found in models/.")
        checkpoint = torch.load(model_path, map_location="cpu")
        input_dim = int(checkpoint["input_dim"])
        module = ActionScorer(input_dim).module()
        module.load_state_dict(checkpoint["model_state"])
        selected_device = select_device(device)
        module.to(selected_device)
        module.eval()
        loaded_model.update({"path": model_path, "module": module, "device": selected_device})
        return {"loaded": True, "model_path": str(model_path), "device": str(selected_device), "input_dim": input_dim}

    def configured_bench_size() -> int:
        config_path = paths.root / "Assets" / "StreamingAssets" / "GameRulesConfig.json"
        try:
            data = json.loads(config_path.read_text(encoding="utf-8"))
            return max(0, min(10, int(data.get("benchSize", 3))))
        except Exception:
            return 3

    @app.get("/", response_class=HTMLResponse)
    def index():
        return HTML

    @app.get("/api/status")
    def status():
        stats = dict(dataset_cache)
        idx = decision_file_index()
        src_counts = dict(idx["sources"])
        stats["decision_files"] = sum(src_counts.values())
        stats["sources"] = src_counts
        stats.update({
            "cards_loaded": len(catalog),
            "decks_available": len(list(paths.decks_dir.glob("*.json"))) if paths.decks_dir.exists() else 0,
            "state_dim": encoder.state_dim,
            "action_dim": encoder.action_dim,
            "input_dim": encoder.state_dim + encoder.action_dim,
            "bench_size": configured_bench_size(),
            "root": str(paths.root),
            "ml_root": str(paths.ml_dir),
            "build_root": str(paths.build_root) if paths.build_root else None,
            "cards_dir": str(paths.cards_dir),
            "decks_dir": str(paths.decks_dir),
            "logs_ml_dir": str(paths.logs_ml_dir),
            "training": process_status("train"),
            "setup": process_status("setup"),
            "evaluation": process_status("eval"),
            "log_sync": "running" if sync_state["running"] else "idle",
            "log_sync_summary": sync_state["summary"],
            "environment": environment_status(),
            "setup_log": str(logs["setup"]) if logs["setup"] else None,
            "train_log": str(logs["train"]) if logs["train"] else None,
            "eval_log": str(logs["eval"]) if logs["eval"] else None,
            "loaded_model": str(loaded_model["path"]) if loaded_model["path"] else None,
            "loaded_device": str(loaded_model["device"]) if loaded_model["device"] else None,
            "curriculum": dict(curriculum_state),
        })
        return stats

    def _normalize_logs_input(raw: str) -> Path:
        """Accept either a 'Logs Export/ML' folder or a build root that contains one."""
        p = Path(raw).expanduser()
        already_ml = p.name == "ML" and p.parent.name == "Logs Export"
        if not already_ml and (p / "Logs Export" / "ML").exists():
            p = p / "Logs Export" / "ML"
        return p

    def _path_report(p) -> dict[str, Any]:
        dec = p.decisions_dir
        n_files = len(list(dec.rglob("*_decisions.jsonl"))) if dec.exists() else 0
        return {
            "root": str(p.root),
            "build_root": str(p.build_root) if p.build_root else None,
            "logs_ml_dir": str(p.logs_ml_dir),
            "decisions_dir": str(p.decisions_dir),
            "games_jsonl": str(p.games_jsonl),
            "cards_dir": str(p.cards_dir),
            "decks_dir": str(p.decks_dir),
            "logs_exists": p.logs_ml_dir.exists(),
            "decisions_exists": dec.exists(),
            "games_jsonl_exists": p.games_jsonl.exists(),
            "cards_exists": p.cards_dir.exists(),
            "decision_files": n_files,
        }

    def apply_data_paths(new_overrides: dict[str, Any]) -> dict[str, Any]:
        """Rebind the data paths live. Falsy values clear that override (back to startup default)."""
        nonlocal paths, catalog, encoder, ui_overrides
        merged = dict(ui_overrides)
        for key, value in new_overrides.items():
            if value:
                merged[key] = str(value)
            else:
                merged.pop(key, None)
        candidate = _resolve(merged)
        cards_changed = candidate.cards_dir != paths.cards_dir
        paths = candidate
        ui_overrides = merged
        paths.runs_dir.mkdir(parents=True, exist_ok=True)
        paths.models_dir.mkdir(parents=True, exist_ok=True)
        if cards_changed and paths.cards_dir.exists():
            try:
                catalog = CardCatalog.load(paths.cards_dir)
                encoder = FeatureEncoder(spec=spec, catalog=catalog)
            except Exception:
                pass
        invalidate_decision_file_cache()
        card_usage_cache.update({"stamp": None, "data": None})
        card_usage_file_cache.clear()
        # Reload the persisted per-file aggregates on next use: entries are keyed by absolute
        # path and stamp-checked, so files under an unchanged location are still reused.
        card_usage_disk["loaded"] = False
        analysis_cache.update({"stamp": None, "games": None, "by_filter": {}})
        replay_index_cache.update({"stamp": None, "data": None})
        dataset_cache.update({
            "decision_files": len(iter_decision_files(paths.decisions_dir)),
            "games_with_metadata": None, "decision_records": None,
            "usable_decisions": None, "invalid_decisions": None,
            "invalid_reasons": {}, "categories": {},
            "records_without_game_metadata": None, "last_scan_unix_ms": None,
        })
        try:
            ui_paths_file.write_text(json.dumps(ui_overrides, indent=2))
        except Exception:
            pass
        return _path_report(paths)

    @app.get("/api/paths")
    def get_data_paths():
        return {
            "current": _path_report(paths),
            "overrides": ui_overrides,
            "defaults": {k: (str(v) if v else None) for k, v in startup_paths.items()},
            "mirror_default": (
                os.environ.get("TCG_DECISIONS_MIRROR_DIR")
                or os.environ.get("TCG_MIRROR_DECISIONS_DIR")
                or ""
            ),
        }

    @app.post("/api/paths/check")
    def check_data_path(payload: dict | None = None):
        payload = payload or {}
        raw = str(payload.get("logs_dir") or "").strip()
        if not raw:
            return {"ok": False, "error": "empty path"}
        ml = _normalize_logs_input(raw)
        dec = ml / "Decisions"
        n = len(list(dec.rglob("*_decisions.jsonl"))) if dec.exists() else 0
        return {
            "ok": ml.exists(),
            "resolved_logs_ml_dir": str(ml.resolve()) if ml.exists() else str(ml),
            "exists": ml.exists(),
            "decisions_exists": dec.exists(),
            "decision_files": n,
            "games_jsonl_exists": (ml / "games.jsonl").exists(),
        }

    @app.post("/api/paths")
    def set_data_paths(payload: dict | None = None):
        payload = payload or {}
        if payload.get("reset"):
            report = apply_data_paths({k: None for k in startup_paths})
            return {"applied": True, **report}
        overrides: dict[str, Any] = {}
        raw_logs = str(payload.get("logs_dir") or "").strip()
        if raw_logs:
            ml = _normalize_logs_input(raw_logs)
            if not ml.exists():
                return {"applied": False, "error": f"Path does not exist: {ml}"}
            overrides["logs_dir"] = str(ml)
        elif "logs_dir" in payload:
            overrides["logs_dir"] = None
        for key in ("cards_dir", "decks_dir", "root", "ml_root", "build_root"):
            val = str(payload.get(key) or "").strip()
            if val:
                vp = Path(val).expanduser()
                if not vp.exists():
                    return {"applied": False, "error": f"Path does not exist: {vp}"}
                overrides[key] = str(vp)
            elif key in payload:
                overrides[key] = None
        if not any(overrides.values()) and not overrides:
            return {"applied": False, "error": "no paths provided", **_path_report(paths)}
        report = apply_data_paths(overrides)
        return {"applied": True, **report}

    @app.get("/api/dataset/sources")
    def dataset_sources():
        # Cached per-context decision-log sources for the training selector.
        # "legacy" = files in the Decisions/ root (older logs / SMB-synced), default-off in the UI.
        counts = dict(decision_file_index()["sources"])

        def _rank(name: str):
            top = name.split("/", 1)[0]
            pri = {"benchmark": 0, "interactive": 1, "received": 2, "legacy": 9}.get(top, 3)
            return (pri, name)

        names = sorted(counts, key=_rank)
        return {
            "sources": [{"name": s, "files": counts[s], "legacy": s == "legacy"} for s in names],
            "total_files": sum(counts.values()),
        }

    @app.post("/api/dataset/scan")
    def dataset_scan():
        invalidate_decision_file_cache()
        # Incremental: per-file aggregates are cached on disk (decision files are immutable),
        # so a rescan only parses files added/changed since the previous scan.
        stats = scan_dataset_incremental(
            paths.decisions_dir, paths.games_jsonl, cache_path=paths.runs_dir / "dataset_scan_cache.json"
        )
        stats["last_scan_unix_ms"] = int(time.time() * 1000)
        dataset_cache.clear()
        dataset_cache.update(stats)
        invalidate_decision_file_cache()
        return stats

    @app.get("/api/environment")
    def environment():
        return environment_status()

    @app.post("/api/advisor/event")
    def advisor_event(payload: dict[str, Any]):
        event = dict(payload or {})
        event["timestamp_unix_ms"] = int(time.time() * 1000)
        advisor_events.append(event)
        del advisor_events[:-200]
        return {"ok": True, "events": len(advisor_events)}

    @app.get("/api/advisor/events")
    def get_advisor_events(limit: int = 80):
        limit = max(1, min(int(limit), 200))
        return {"events": advisor_events[-limit:]}

    @app.post("/api/advisor/clear")
    def clear_advisor_events():
        advisor_events.clear()
        return {"ok": True}

    def games_file_stamp() -> tuple[int, int] | None:
        """(size, mtime_ns) of games.jsonl, or None if it can't be stat'd. games.jsonl is append-only,
        so any add/edit/delete changes both fields — a matching stamp means identical content."""
        try:
            st = paths.games_jsonl.stat()
        except OSError:
            return None
        return (st.st_size, st.st_mtime_ns)

    @app.get("/api/analysis")
    def get_analysis(source: str = "all", matchup: str = "all"):
        if source not in ("all", "benchmark", "interactive"):
            source = "all"
        if not paths.games_jsonl.exists():
            return {"ok": False, "reason": "games.jsonl not found"}
        matchup = matchup or "all"

        # Reparse games.jsonl only when its stamp changes; per-filter analyze_games results are cached
        # within the same stamp so revisiting a filter / Refresh / page-load reuse is instant.
        stamp = games_file_stamp()
        if analysis_cache.get("stamp") != stamp or analysis_cache.get("games") is None:
            analysis_cache["games"] = [rec for _, rec in iter_jsonl(paths.games_jsonl)]
            analysis_cache["by_filter"] = {}
            analysis_cache["stamp"] = stamp

        key = (source, matchup)
        result = analysis_cache["by_filter"].get(key)
        if result is None:
            result = analyze_games(analysis_cache["games"], source=source, matchup=matchup)
            analysis_cache["by_filter"][key] = result

        if result.get("ok"):
            # card_usage has its own (now incremental) stamp-based cache.
            result = {**result, "card_usage": analyze_card_usage()}
        return result

    @app.get("/api/card-image/{card_name:path}")
    def get_card_image(card_name: str):
        card = catalog.get_by_name(card_name)
        path = card_image_path(card)
        if path is None or not path.exists():
            raise HTTPException(status_code=404, detail="Card image not found")
        return FileResponse(path)

    @app.post("/api/setup/start")
    def setup_start(payload: dict | None = None):
        if is_running("setup"):
            return {"started": False, "status": "already-running", "log": str(logs["setup"])}
        payload = payload or {}
        profile = str(payload.get("profile") or "auto")
        if profile not in {"cpu", "gpu", "auto"}:
            profile = "auto"
        setup_script = paths.ml_dir / "scripts" / "setup_env.py"
        if setup_script.exists():
            cmd = [
                sys.executable,
                str(setup_script),
                "--profile",
                profile,
            ]
        else:
            cmd = [sys.executable, "-m", "pip", "install", "-r", "requirements.txt"]
        log_handle = open_log("setup")
        log_handle.write(" ".join(cmd) + "\n")
        if not setup_script.exists():
            log_handle.write(f"{setup_script} not found; falling back to requirements.txt\n")
        processes["setup"] = subprocess.Popen(
            cmd,
            cwd=str(paths.ml_dir),
            stdout=log_handle,
            stderr=subprocess.STDOUT,
            text=True,
        )
        return {"started": True, "pid": processes["setup"].pid, "profile": profile, "log": str(logs["setup"])}

    @app.post("/api/setup/stop")
    def setup_stop():
        return stop_process("setup")

    @app.get("/api/models")
    def models():
        import json as _json

        items = []
        for path in paths.models_dir.glob("*.pt"):
            meta = None
            base_model_exists = None
            sidecar = path.with_suffix(".json")
            if sidecar.exists():
                try:
                    meta = _json.loads(sidecar.read_text(encoding="utf-8"))
                except (OSError, ValueError):
                    meta = None
            if isinstance(meta, dict) and meta.get("from_model"):
                # The stored from_model path is recorded verbatim at train time, so a relative or
                # absolute path from a different CWD won't resolve here. Fall back to matching the
                # base model by filename inside models_dir before declaring it missing.
                raw_from = str(meta["from_model"])
                base_model_exists = (
                    Path(raw_from).exists()
                    or (paths.models_dir / Path(raw_from).name).exists()
                )
            items.append(
                {
                    "path": str(path),
                    "trained_unix_ms": model_trained_unix_ms(path, meta),
                    "modified_unix_ms": int(path.stat().st_mtime * 1000),
                    "bytes": path.stat().st_size,
                    "meta": meta,
                    "base_model_exists": base_model_exists,
                }
            )
        items.sort(key=lambda item: item["trained_unix_ms"], reverse=True)
        return {"models": items}

    @app.post("/api/load-model")
    def api_load_model(payload: dict | None = None):
        payload = payload or {}
        try:
            path = Path(payload["path"]) if payload.get("path") else None
            return load_model(path=path, device=str(payload.get("device") or "auto"))
        except Exception as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc

    @app.post("/api/delete-model")
    def api_delete_model(payload: dict | None = None):
        payload = payload or {}
        if not payload.get("path"):
            raise HTTPException(status_code=400, detail="path is required")
        model_path = Path(payload["path"]).resolve()
        models_root = paths.models_dir.resolve()
        if models_root not in model_path.parents:
            raise HTTPException(status_code=400, detail="path is outside the models directory")
        if model_path.suffix != ".pt" or not model_path.exists():
            raise HTTPException(status_code=404, detail="model not found")
        # Unload first if the file being deleted is the currently loaded model.
        if loaded_model["path"] and Path(loaded_model["path"]).resolve() == model_path:
            loaded_model.update({"path": None, "module": None, "device": None})
        try:
            model_path.unlink()
            model_path.with_suffix(".json").unlink(missing_ok=True)  # remove metadata sidecar too
        except Exception as exc:
            raise HTTPException(status_code=400, detail=str(exc)) from exc
        return {"deleted": True, "path": str(model_path)}

    def path_cli_args() -> list[str]:
        # Pass the dashboard's currently-resolved data paths explicitly to subprocesses so
        # they read the same Logs Export/ML / Cards / Decks even when overridden from the UI.
        return [
            "--root", str(paths.root),
            "--logs-dir", str(paths.logs_ml_dir),
            "--cards-dir", str(paths.cards_dir),
            "--decks-dir", str(paths.decks_dir),
        ]

    @app.post("/api/train/start")
    def train_start(payload: dict | None = None):
        if is_running("train"):
            return {"started": False, "status": "already-running", "log": str(logs["train"])}
        payload = payload or {}
        curriculum_state.update({"active": False, "stage": 0, "stage1_model": None, "cancelled": False})
        cmd = [
            sys.executable,
            str(paths.ml_dir / "scripts" / "train_bc.py"),
            "--max-games",
            str(max(0, int(payload.get("max_games") or 0))),
            "--device",
            str(payload.get("device") or "auto"),
            "--epochs",
            str(int(payload.get("epochs") or 3)),
            "--val-ratio",
            str(float(payload.get("val_ratio") if payload.get("val_ratio") is not None else 0.1)),
            "--seed",
            str(int(payload.get("seed") if payload.get("seed") is not None else 1234)),
            "--log-every",
            str(int(payload.get("log_every") or 1000)),
            "--patience",
            str(max(0, int(payload.get("patience") or 0))),
            *path_cli_args(),
        ]
        if payload.get("winners_only"):
            cmd.append("--winners-only")
        profiles = [str(p).strip() for p in (payload.get("profiles") or []) if str(p).strip()]
        if profiles:
            cmd += ["--profile", *profiles]
        # Decision-log sources to train on (Decisions/ subfolders). Omitted => train_bc default
        # (every non-legacy source). "all" => every source incl. the legacy root.
        sources = payload.get("sources")
        if isinstance(sources, list):
            clean = [str(s).strip() for s in sources if str(s).strip()]
            if clean:
                cmd += ["--sources", *clean]
        # Fine-tuning: optional starting checkpoint and learning rate.
        from_model = payload.get("from_model")
        if from_model and from_model != "none":
            resolved = from_model if from_model != "latest" else (
                str(latest_model_path()) if latest_model_path() else None
            )
            if resolved:
                cmd += ["--from-model", resolved]
        lr = payload.get("lr")
        if lr is not None:
            cmd += ["--lr", str(float(lr))]
        grad_accum = payload.get("grad_accum")
        if grad_accum is not None and int(grad_accum) > 1:
            cmd += ["--grad-accum", str(int(grad_accum))]
        log_handle = open_log("train")
        log_handle.write(" ".join(cmd) + "\n")
        processes["train"] = subprocess.Popen(
            cmd,
            cwd=str(paths.ml_dir),
            stdout=log_handle,
            stderr=subprocess.STDOUT,
            text=True,
            **new_group_kwargs,
        )
        return {"started": True, "pid": processes["train"].pid, "log": str(logs["train"])}

    @app.post("/api/train/stop")
    def train_stop():
        # Graceful: train_bc.py saves the model learned so far, then exits.
        # Also cancel a running curriculum so the watcher thread doesn't start Stage 2.
        curriculum_state["cancelled"] = True
        return stop_process("train", graceful=True)

    @app.post("/api/train/curriculum/start")
    def curriculum_start(payload: dict | None = None):
        """Two-stage curriculum: Stage 1 trains on all decisions; Stage 2 fine-tunes on winners.
        Stage 1 uses the standard training params; Stage 2 params are prefixed s2_*.
        When Stage 1 finishes successfully, a background thread starts Stage 2 automatically."""
        if is_running("train"):
            return {"started": False, "status": "already-running"}
        payload = payload or {}

        def build_cmd(p: dict, from_model_path: str | None = None) -> list[str]:
            cmd = [
                sys.executable,
                str(paths.ml_dir / "scripts" / "train_bc.py"),
                "--max-games", str(max(0, int(p.get("max_games") or 0))),
                "--device", str(p.get("device") or "auto"),
                "--epochs", str(int(p.get("epochs") or 3)),
                "--val-ratio", str(float(p.get("val_ratio") if p.get("val_ratio") is not None else 0.1)),
                "--seed", str(int(p.get("seed") if p.get("seed") is not None else 1234)),
                "--log-every", str(int(p.get("log_every") or 1000)),
                "--patience", str(max(0, int(p.get("patience") or 4))),
                "--lr", str(float(p.get("lr") or 1e-4)),
                *path_cli_args(),
            ]
            if p.get("winners_only"):
                cmd.append("--winners-only")
            src = p.get("sources")
            if isinstance(src, list):
                clean = [str(s).strip() for s in src if str(s).strip()]
                if clean:
                    cmd += ["--sources", *clean]
            ga = p.get("grad_accum")
            if ga is not None and int(ga) > 1:
                cmd += ["--grad-accum", str(int(ga))]
            if from_model_path:
                cmd += ["--from-model", from_model_path]
            return cmd

        # Stage 1 params from top-level payload; Stage 2 params from s2_* keys.
        s2 = {k[3:]: v for k, v in payload.items() if k.startswith("s2_")}
        # Stage 2 winners_only defaults to True (the whole point of fine-tuning).
        s2.setdefault("winners_only", True)
        s2.setdefault("epochs", 5)
        s2.setdefault("lr", 1e-5)
        s2.setdefault("device", payload.get("device", "auto"))
        s2.setdefault("val_ratio", payload.get("val_ratio", 0.1))
        s2.setdefault("seed", payload.get("seed", 1234))
        s2.setdefault("log_every", payload.get("log_every", 1000))
        s2.setdefault("patience", payload.get("patience", 4))
        s2.setdefault("sources", payload.get("sources"))

        curriculum_state.update({"active": True, "stage": 1, "stage1_model": None, "cancelled": False})

        log_handle = open_log("train")
        s1_cmd = build_cmd(payload)
        log_handle.write("# Stage 1 / 2 — pre-train on all decisions\n" + " ".join(s1_cmd) + "\n")
        processes["train"] = subprocess.Popen(
            s1_cmd, cwd=str(paths.ml_dir),
            stdout=log_handle, stderr=subprocess.STDOUT,
            text=True, **new_group_kwargs,
        )

        import threading

        def run_stage2():
            proc = processes["train"]
            if proc:
                proc.wait()
            if curriculum_state.get("cancelled"):
                curriculum_state.update({"active": False, "stage": 0})
                return
            exit_code = proc.returncode if proc else -1
            if exit_code != 0:
                curriculum_state.update({"active": False, "stage": 0})
                return
            # Find the model Stage 1 just saved (newest .pt).
            import time as _time
            _time.sleep(0.5)  # brief wait for filesystem flush
            s1_model = latest_model_path()
            if not s1_model:
                curriculum_state.update({"active": False, "stage": 0})
                return
            curriculum_state["stage1_model"] = str(s1_model)
            curriculum_state["stage"] = 2
            if curriculum_state.get("cancelled"):
                curriculum_state.update({"active": False, "stage": 0})
                return
            s2_cmd = build_cmd(s2, from_model_path=str(s1_model))
            log_handle.write("\n# Stage 2 / 2 — fine-tune on winners\n" + " ".join(s2_cmd) + "\n")
            log_handle.flush()
            processes["train"] = subprocess.Popen(
                s2_cmd, cwd=str(paths.ml_dir),
                stdout=log_handle, stderr=subprocess.STDOUT,
                text=True, **new_group_kwargs,
            )
            processes["train"].wait()
            curriculum_state.update({"active": False})

        threading.Thread(target=run_stage2, daemon=True).start()
        return {
            "started": True, "curriculum": True, "stage": 1,
            "pid": processes["train"].pid, "log": str(logs["train"]),
        }

    @app.post("/api/train/curriculum/stop")
    def curriculum_stop():
        curriculum_state["cancelled"] = True
        return stop_process("train", graceful=True)

    @app.post("/api/evaluate/start")
    def evaluate_start(payload: dict | None = None):
        if is_running("eval"):
            return {"started": False, "status": "already-running", "log": str(logs["eval"])}
        payload = payload or {}
        cmd = [
            sys.executable,
            str(paths.ml_dir / "scripts" / "evaluate_model.py"),
            "--max-decisions",
            str(int(payload.get("max_decisions") or 50000)),
            "--device",
            str(payload.get("device") or "auto"),
            "--log-every",
            str(int(payload.get("log_every") or 1000)),
            *path_cli_args(),
        ]
        log_handle = open_log("eval")
        log_handle.write(" ".join(cmd) + "\n")
        processes["eval"] = subprocess.Popen(
            cmd,
            cwd=str(paths.ml_dir),
            stdout=log_handle,
            stderr=subprocess.STDOUT,
            text=True,
        )
        return {"started": True, "pid": processes["eval"].pid, "log": str(logs["eval"])}

    @app.post("/api/evaluate/stop")
    def evaluate_stop():
        return stop_process("eval")

    @app.post("/api/train")
    def train_legacy(max_games: int = 0, device: str = "auto", epochs: int = 3):
        return train_start({"max_games": max_games, "device": device, "epochs": epochs, "log_every": 1000})

    @app.get("/api/logs/{kind}")
    def get_log(kind: str):
        if kind not in logs:
            raise HTTPException(status_code=404, detail="unknown log kind")
        return {"kind": kind, "path": str(logs[kind]) if logs[kind] else None, "text": tail(logs[kind])}

    # ---- Server log sync (SMB → local Decisions) ----------------------------------------
    # Optional private-lab source. The same share resolves to different local paths per OS.
    SMB_HOST = os.environ.get("TCG_SMB_HOST", "example.invalid")
    SMB_SHARE = os.environ.get("TCG_SMB_SHARE", "tcg_station_logs")
    SMB_SUBPATH = ("tcg_station_log_server", "received", "decisions")
    # The ML-logs-mirror target is no longer hardcoded: the user supplies the path to a
    # logs copy in the dashboard (or via TCG_DECISIONS_MIRROR_DIR) and its subfolders are
    # auto-detected. See normalize_mirror_path / run_decisions_mirror_sync below.
    PROJECT_JSONS_SHORTCUT_ID = os.environ.get(
        "TCG_PROJECT_JSONS_SHORTCUT_ID",
        "PUBLIC_PLACEHOLDER_CHANGE_ME",
    )
    GOOGLE_DRIVE_ACCOUNT = os.environ.get("TCG_GOOGLE_DRIVE_ACCOUNT", "account")

    def timestamp_for_archive() -> str:
        return time.strftime("%Y%m%d_%H%M%S")

    def zip_folder(src: Path, archive_dir: Path, prefix: str, ts: str) -> Path | None:
        if not src.exists():
            return None
        archive_dir.mkdir(parents=True, exist_ok=True)
        target = archive_dir / f"{prefix}_{ts}.zip"
        with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_DEFLATED) as zf:
            for file in src.rglob("*"):
                if not file.is_file():
                    continue
                zf.write(file, file.relative_to(src.parent))
        return target

    def resolve_project_jsons_source() -> tuple[Path | None, str]:
        env = os.environ.get("TCG_PROJECT_JSONS_DIR") or os.environ.get("TCG_STATION_PROJECT_JSONS_DIR")
        candidates: list[Path] = []
        if env:
            candidates.append(Path(env).expanduser())
        home = Path.home()
        candidates.extend([
            home / "Library" / "CloudStorage" / f"GoogleDrive-{GOOGLE_DRIVE_ACCOUNT}" / ".shortcut-targets-by-id" / PROJECT_JSONS_SHORTCUT_ID / "ProjektJSONs",
            home / "Google Drive" / ".shortcut-targets-by-id" / PROJECT_JSONS_SHORTCUT_ID / "ProjektJSONs",
            home / "My Drive" / ".shortcut-targets-by-id" / PROJECT_JSONS_SHORTCUT_ID / "ProjektJSONs",
            home / f"GoogleDrive-{GOOGLE_DRIVE_ACCOUNT}" / ".shortcut-targets-by-id" / PROJECT_JSONS_SHORTCUT_ID / "ProjektJSONs",
        ])
        for candidate in candidates:
            if candidate.exists():
                return candidate, str(candidate)
        return None, str(candidates[0]) if candidates else "TCG_PROJECT_JSONS_DIR"

    def card_target_subdir(src: Path, existing_locations: dict[str, str]) -> str:
        try:
            data = json.loads(src.read_text(encoding="utf-8-sig"))
        except Exception:
            data = {}
        card_type = str(data.get("cardType") or data.get("card_type") or "").strip().lower()
        trainer_subtype = str(data.get("trainerSubType") or data.get("trainer_subtype") or "").strip().lower()
        if card_type == "pokemon":
            return "Pokemons"
        if card_type == "trainer":
            if trainer_subtype == "supporter":
                return "Supporters"
            if trainer_subtype == "tool":
                return "Tools"
            if trainer_subtype == "stadium":
                return "Stadiums"
            return "Items"
        return existing_locations.get(src.name, "Pokemons")

    def existing_card_locations(cards_dir: Path) -> dict[str, str]:
        locations: dict[str, str] = {}
        for file in cards_dir.rglob("*.json"):
            try:
                rel = file.relative_to(cards_dir)
            except ValueError:
                continue
            if len(rel.parts) >= 2:
                locations.setdefault(file.name, rel.parts[0])
        return locations

    def clear_json_files(root_dir: Path) -> int:
        removed = 0
        if not root_dir.exists():
            return 0
        for file in sorted(root_dir.rglob("*.json")):
            if not file.is_file():
                continue
            file.unlink()
            removed += 1
        return removed

    def counter_increment(counter: dict[str, int], key: Any, amount: int = 1) -> None:
        label = str(key if key not in (None, "") else "unknown")
        counter[label] = counter.get(label, 0) + amount

    def safe_int(value: Any, default: int = 0) -> int:
        try:
            return int(value)
        except (TypeError, ValueError):
            return default

    def summarize_cards(cards_dir: Path) -> dict[str, Any]:
        files = sorted(p for p in cards_dir.rglob("*.json") if p.is_file()) if cards_dir.exists() else []
        by_type: dict[str, int] = {}
        by_trainer_subtype: dict[str, int] = {}
        by_energy_type: dict[str, int] = {}
        by_stage: dict[str, int] = {}
        retreat_costs: dict[str, int] = {}
        hp_values: list[int] = []
        attack_count_values: list[int] = []
        effect_types: dict[str, int] = {}
        attacks_with_effects = 0
        invalid: list[dict[str, str]] = []
        valid_count = 0
        for file in files:
            try:
                data = json.loads(file.read_text(encoding="utf-8-sig"))
            except Exception as exc:  # noqa: BLE001
                invalid.append({"file": str(file.relative_to(cards_dir)), "error": str(exc)})
                continue
            valid_count += 1
            card_type = data.get("cardType")
            trainer_subtype = data.get("trainerSubType")
            counter_increment(by_type, card_type)
            if trainer_subtype:
                counter_increment(by_trainer_subtype, trainer_subtype)
            if data.get("type"):
                counter_increment(by_energy_type, data.get("type"))
            if card_type == "Pokemon":
                counter_increment(by_stage, data.get("stage"))
                if isinstance(data.get("hp"), (int, float)):
                    hp_values.append(int(data["hp"]))
                if isinstance(data.get("retreatCost"), (int, float)):
                    counter_increment(retreat_costs, int(data["retreatCost"]))
            effects = data.get("effects") if isinstance(data.get("effects"), list) else []
            for effect in effects:
                if isinstance(effect, dict):
                    counter_increment(effect_types, effect.get("cardEffectType"))
            attacks = data.get("attacks") if isinstance(data.get("attacks"), list) else []
            attack_count_values.append(len(attacks))
            for attack in attacks:
                if not isinstance(attack, dict):
                    continue
                attack_effects = attack.get("effects") if isinstance(attack.get("effects"), list) else []
                if attack_effects:
                    attacks_with_effects += 1
                for effect in attack_effects:
                    if isinstance(effect, dict):
                        counter_increment(effect_types, effect.get("cardEffectType"))
        return {
            "file_count": len(files),
            "valid_count": valid_count,
            "invalid_count": len(invalid),
            "invalid_files": invalid[:100],
            "by_card_type": by_type,
            "by_trainer_subtype": by_trainer_subtype,
            "by_pokemon_type": by_energy_type,
            "by_stage": by_stage,
            "retreat_costs": retreat_costs,
            "hp": describe(hp_values),
            "attacks_per_card": describe(attack_count_values),
            "effect_types": effect_types,
            "attacks_with_effects": attacks_with_effects,
        }

    def summarize_decks(decks_dir: Path, card_catalog: CardCatalog) -> dict[str, Any]:
        files = sorted(p for p in decks_dir.rglob("*.json") if p.is_file()) if decks_dir.exists() else []
        energy_types: dict[str, int] = {}
        deck_sizes: list[int] = []
        unique_card_counts: list[int] = []
        card_frequency: dict[str, int] = {}
        card_deck_presence: dict[str, int] = {}
        type_mix_totals: dict[str, int] = {}
        invalid: list[dict[str, str]] = []
        valid_count = 0
        for file in files:
            try:
                data = json.loads(file.read_text(encoding="utf-8-sig"))
            except Exception as exc:  # noqa: BLE001
                invalid.append({"file": str(file.relative_to(decks_dir)), "error": str(exc)})
                continue
            valid_count += 1
            cards = data.get("cards") if isinstance(data.get("cards"), list) else []
            deck_size = 0
            unique_ids: set[str] = set()
            type_mix: dict[str, int] = {}
            for entry in cards:
                if not isinstance(entry, dict):
                    continue
                card_id = str(entry.get("cardId") or "").strip()
                count = safe_int(entry.get("count"), 0)
                if not card_id:
                    continue
                deck_size += count
                unique_ids.add(card_id)
                card_frequency[card_id] = card_frequency.get(card_id, 0) + count
                card_deck_presence[card_id] = card_deck_presence.get(card_id, 0) + 1
                card = card_catalog.by_id.get(card_id.lower())
                card_type = str((card or {}).get("cardType") or "unknown")
                type_mix[card_type] = type_mix.get(card_type, 0) + count
                type_mix_totals[card_type] = type_mix_totals.get(card_type, 0) + count
            for energy in data.get("energyTypes") if isinstance(data.get("energyTypes"), list) else []:
                counter_increment(energy_types, energy)
            deck_sizes.append(deck_size)
            unique_card_counts.append(len(unique_ids))
        top_cards = sorted(card_frequency.items(), key=lambda kv: (-kv[1], kv[0]))[:50]
        most_common_presence = sorted(card_deck_presence.items(), key=lambda kv: (-kv[1], kv[0]))[:50]
        return {
            "file_count": len(files),
            "valid_count": valid_count,
            "invalid_count": len(invalid),
            "invalid_files": invalid[:100],
            "deck_size": describe(deck_sizes),
            "unique_cards_per_deck": describe(unique_card_counts),
            "energy_types": energy_types,
            "type_mix_totals": type_mix_totals,
            "top_cards_by_copies": [{"card_id": k, "copies": v} for k, v in top_cards],
            "top_cards_by_deck_presence": [{"card_id": k, "decks": v} for k, v in most_common_presence],
        }

    def summarize_games_file() -> dict[str, Any]:
        games = list(load_games(paths.games_jsonl).values())
        result = analyze_games(games, source="all", matchup="all")
        result["source_views"] = {
            "benchmark": analyze_games(games, source="benchmark", matchup="all"),
            "interactive": analyze_games(games, source="interactive", matchup="all"),
        }
        return result

    def summarize_model_baseline() -> dict[str, Any]:
        models: list[dict[str, Any]] = []
        for path in sorted(paths.models_dir.glob("*.pt"), key=lambda p: p.stat().st_mtime, reverse=True):
            meta = None
            sidecar = path.with_suffix(".json")
            if sidecar.exists():
                try:
                    meta = json.loads(sidecar.read_text(encoding="utf-8"))
                except Exception:
                    meta = None
            models.append({
                "file": path.name,
                "bytes": path.stat().st_size,
                "modified_unix_ms": int(path.stat().st_mtime * 1000),
                "trained_unix_ms": model_trained_unix_ms(path, meta if isinstance(meta, dict) else None),
                "input_dim": (meta or {}).get("input_dim") if isinstance(meta, dict) else None,
                "profile": (meta or {}).get("profile") if isinstance(meta, dict) else None,
                "sources": (meta or {}).get("sources") if isinstance(meta, dict) else None,
                "from_model": (meta or {}).get("from_model") if isinstance(meta, dict) else None,
                "val_acc": (meta or {}).get("val_acc") if isinstance(meta, dict) else None,
                "val_macro_acc": (meta or {}).get("val_macro_acc") if isinstance(meta, dict) else None,
                "best_epoch": (meta or {}).get("best_epoch") if isinstance(meta, dict) else None,
                "epochs_completed": (meta or {}).get("epochs_completed") if isinstance(meta, dict) else None,
                "max_games": (meta or {}).get("max_games") if isinstance(meta, dict) else None,
                "winners_only": (meta or {}).get("winners_only") if isinstance(meta, dict) else None,
                "patch_no": (meta or {}).get("patch_no") if isinstance(meta, dict) else None,
                "patch_ts": (meta or {}).get("patch_ts") if isinstance(meta, dict) else None,
                "has_sidecar": isinstance(meta, dict),
            })
        ranked = sorted(
            [m for m in models if m.get("val_macro_acc") is not None],
            key=lambda m: (m.get("val_macro_acc") or 0, m.get("val_acc") or 0),
            reverse=True,
        )
        return {"model_count": len(models), "models": models, "best_by_macro": ranked[:10]}

    def standings_cutoffs(deck_winrate: list[dict[str, Any]]) -> dict[str, Any]:
        out: dict[str, Any] = {}
        rows = list(deck_winrate or [])
        for cutoff in (5, 10, 30, 100):
            eligible = [r for r in rows if int(r.get("games") or 0) >= cutoff]
            top = sorted(eligible, key=lambda r: (r.get("win_rate") if r.get("win_rate") is not None else -1.0, r.get("games") or 0), reverse=True)[:10]
            bottom = sorted(eligible, key=lambda r: (r.get("win_rate") if r.get("win_rate") is not None else 2.0, -(r.get("games") or 0)))[:10]
            out[str(cutoff)] = {"eligible_rows": len(eligible), "top": top, "bottom": bottom}
        return out

    def summarize_meta_from_games(games: list[dict[str, Any]]) -> dict[str, Any]:
        deck_counts: dict[str, int] = {}
        matchup_counts: dict[str, int] = {}
        deck_pair_counts: dict[str, int] = {}
        profile_counts: dict[str, int] = {}
        benchmark_flags: dict[str, int] = {}
        timestamps: list[int] = []
        for g in games:
            for deck_key in ("deck_a", "deck_b"):
                deck = g.get(deck_key)
                if deck:
                    counter_increment(deck_counts, deck)
            if g.get("deck_a") and g.get("deck_b"):
                pair = " vs ".join(sorted([str(g["deck_a"]), str(g["deck_b"])]))
                counter_increment(deck_pair_counts, pair)
            counter_increment(matchup_counts, matchup_key(g))
            for brain_key in ("brain_a", "brain_b"):
                brain = str(g.get(brain_key) or "")
                if brain.startswith("Algorithm:"):
                    counter_increment(profile_counts, brain.split(":", 1)[1])
            flag = g.get("is_benchmark")
            counter_increment(benchmark_flags, "benchmark" if flag is True else "interactive" if flag is False else "unknown")
            for key in ("created_unix_ms", "timestamp_unix_ms", "ended_unix_ms"):
                value = g.get(key)
                if isinstance(value, (int, float)) and value > 0:
                    timestamps.append(int(value))
                    break

        total_deck_seats = sum(deck_counts.values()) or 1
        ranked_decks = sorted(deck_counts.items(), key=lambda kv: (-kv[1], kv[0]))
        concentration = {}
        for n in (1, 3, 5, 10):
            concentration[f"top_{n}_share"] = sum(v for _k, v in ranked_decks[:n]) / total_deck_seats
        return {
            "deck_seat_counts": [{"deck": k, "count": v, "share": v / total_deck_seats} for k, v in ranked_decks],
            "meta_concentration": concentration,
            "matchup_counts": dict(sorted(matchup_counts.items(), key=lambda kv: (-kv[1], kv[0]))),
            "deck_pair_counts": dict(sorted(deck_pair_counts.items(), key=lambda kv: (-kv[1], kv[0]))[:100]),
            "algorithm_profile_counts": dict(sorted(profile_counts.items(), key=lambda kv: (-kv[1], kv[0]))),
            "benchmark_flags": benchmark_flags,
            "game_time_range": {
                "oldest_unix_ms": min(timestamps) if timestamps else None,
                "newest_unix_ms": max(timestamps) if timestamps else None,
            },
        }

    def summarize_dataset_quality_from_cache(cache: dict[str, Any]) -> dict[str, Any]:
        categories = dict(cache.get("categories") or {})
        trainable = {k: v for k, v in categories.items() if ":" not in k}
        skip = {k: v for k, v in categories.items() if k.endswith(":skip")}
        legal_skip = {k: v for k, v in categories.items() if k.endswith(":skip_legal")}
        return {
            "last_scan_unix_ms": cache.get("last_scan_unix_ms"),
            "decision_records": cache.get("decision_records"),
            "usable_decisions": cache.get("usable_decisions"),
            "invalid_decisions": cache.get("invalid_decisions"),
            "non_trainable_records": cache.get("non_trainable_records"),
            "records_without_game_metadata": cache.get("records_without_game_metadata"),
            "trainable_categories": trainable,
            "skip_categories": skip,
            "legal_skip_categories": legal_skip,
            "invalid_reasons": dict(cache.get("invalid_reasons") or {}),
            "source_breakdown": dict(cache.get("source_breakdown") or {}),
        }

    def summarize_game_rules() -> dict[str, Any]:
        config_path = paths.root / "Assets" / "StreamingAssets" / "GameRulesConfig.json"
        try:
            data = json.loads(config_path.read_text(encoding="utf-8-sig"))
        except Exception as exc:  # noqa: BLE001
            return {"available": False, "error": str(exc)}
        keys = [
            "benchSize", "maxTurns", "startingHandSize", "cardsPerTurn", "pointsToWin",
            "allowRetreat", "attachEnergyPerTurn", "firstPlayerCanAttack", "llmProvider",
            "player1DeckName", "player2DeckName",
        ]
        return {"available": True, "config_file": str(config_path), "values": {k: data.get(k) for k in keys if k in data}}

    def summarize_decision_files(index: dict[str, Any]) -> dict[str, Any]:
        files = list(index.get("files") or [])
        total_size = 0
        oldest_mtime = None
        newest_mtime = None
        count = 0
        for file in files:
            if not isinstance(file, Path):
                continue
            try:
                st = file.stat()
            except OSError:
                continue
            count += 1
            total_size += st.st_size
            oldest_mtime = st.st_mtime if oldest_mtime is None else min(oldest_mtime, st.st_mtime)
            newest_mtime = st.st_mtime if newest_mtime is None else max(newest_mtime, st.st_mtime)
        return {
            "decision_files": count,
            "total_size_bytes": total_size,
            "oldest_modified_unix": int(oldest_mtime) if oldest_mtime is not None else None,
            "newest_modified_unix": int(newest_mtime) if newest_mtime is not None else None,
            "sources": dict(index.get("sources") or {}),
        }

    def write_pre_patch_stats(ts: str, source_root: Path, archives: list[str]) -> Path:
        out_dir = paths.root / "PrePatchesData"
        out_dir.mkdir(parents=True, exist_ok=True)
        current_catalog = CardCatalog.load(paths.cards_dir)
        decision_index = decision_file_index(force=True)
        cached_card_usage = card_usage_cache.get("data") if card_usage_cache.get("stamp") == decision_logs_stamp() else None
        games = list(load_games(paths.games_jsonl).values())
        games_analysis = analyze_games(games, source="all", matchup="all")
        games_analysis["source_views"] = {
            "benchmark": analyze_games(games, source="benchmark", matchup="all"),
            "interactive": analyze_games(games, source="interactive", matchup="all"),
        }
        stats = {
            "schema_version": 1,
            "kind": "pre_patch_meta_snapshot",
            "generated_at_unix": int(time.time()),
            "generated_at_local": time.strftime("%Y-%m-%d %H:%M:%S"),
            "patch_source": str(source_root),
            "project_root": str(paths.root),
            "archives_created": archives,
            "cards": summarize_cards(paths.cards_dir),
            "decks": summarize_decks(paths.decks_dir, current_catalog),
            "dataset_cached_scan": dict(dataset_cache),
            "dataset_quality": summarize_dataset_quality_from_cache(dataset_cache),
            "games_analysis": games_analysis,
            "standings_cutoffs": standings_cutoffs(games_analysis.get("deck_winrate") or []),
            "meta_from_games": summarize_meta_from_games(games),
            "model_baseline": summarize_model_baseline(),
            "game_rules": summarize_game_rules(),
            "card_usage": cached_card_usage,
            "card_usage_note": "present only when already computed in this server session; omitted to avoid blocking patch backup on a full decision-log scan",
            "decision_logs": summarize_decision_files(decision_index),
        }
        target = out_dir / f"pre_patch_meta_{ts}.json"
        target.write_text(json.dumps(stats, indent=2, ensure_ascii=False), encoding="utf-8")
        summary = [
            "# Pre-patch meta snapshot",
            "",
            f"- generated: {stats['generated_at_local']}",
            f"- source: `{source_root}`",
            f"- cards: {stats['cards']['valid_count']} valid / {stats['cards']['file_count']} files",
            f"- decks: {stats['decks']['valid_count']} valid / {stats['decks']['file_count']} files",
            f"- games: {stats['games_analysis'].get('n_games', 0)}",
            f"- decision files: {stats['decision_logs'].get('decision_files', 0)}",
            f"- cached usable decisions: {stats['dataset_cached_scan'].get('usable_decisions')}",
            f"- JSON report: `{target.name}`",
        ]
        (out_dir / f"pre_patch_meta_{ts}.md").write_text("\n".join(summary) + "\n", encoding="utf-8")
        return target

    def run_json_patch_sync(backup_logs: bool = False) -> None:
        handle = open_log("sync")

        def w(msg: str) -> None:
            handle.write(msg + "\n")
            handle.flush()

        try:
            w("# Download card/deck JSON patch")
            source_root, hint = resolve_project_jsons_source()
            w(f"source: {hint}")
            if source_root is None:
                w("ERROR: ProjektJSONs source folder is not reachable.")
                w("Set TCG_PROJECT_JSONS_DIR to override the source path on this machine.")
                sync_state["summary"] = {"ok": False, "mode": "json-patch", "error": "ProjektJSONs source unreachable"}
                return
            source_cards = source_root / "CardJSONs"
            source_decks = source_root / "DeckJSONs"
            if not source_cards.exists() or not source_decks.exists():
                w("ERROR: expected CardJSONs and DeckJSONs subfolders in source.")
                sync_state["summary"] = {"ok": False, "mode": "json-patch", "error": "source subfolders missing"}
                return

            project_root = paths.root
            cards_dir = paths.cards_dir
            decks_dir = paths.decks_dir
            archive_dir = project_root / "Backups" / "JsonSync"
            ts = timestamp_for_archive()
            w(f"archive dir: {archive_dir}")
            archived: list[str] = []
            for src, prefix in ((cards_dir, "Cards"), (decks_dir, "Decks")):
                archive = zip_folder(src, archive_dir, prefix, ts)
                if archive:
                    archived.append(str(archive))
                    w(f"archived {src.name}: {archive}")
                else:
                    w(f"archive skipped missing folder: {src}")

            if backup_logs:
                for src, prefix in (
                    (paths.decisions_dir, "LogsExport_ML_Decisions"),
                    (paths.logs_root / "Deckbuilder", "LogsExport_Deckbuilder"),
                ):
                    archive = zip_folder(src, archive_dir, prefix, ts)
                    if archive:
                        archived.append(str(archive))
                        w(f"archived logs {src}: {archive}")
                    else:
                        w(f"log archive skipped missing folder: {src}")
            else:
                w("training log backup skipped by user")

            w("writing pre-patch meta statistics...")
            pre_patch_stats = write_pre_patch_stats(ts, source_root, archived)
            w(f"pre-patch stats: {pre_patch_stats}")

            cards_dir.mkdir(parents=True, exist_ok=True)
            decks_dir.mkdir(parents=True, exist_ok=True)
            locations = existing_card_locations(cards_dir)
            removed_cards = clear_json_files(cards_dir)
            removed_decks = clear_json_files(decks_dir)
            w(f"removed old JSONs: Cards={removed_cards}, Decks={removed_decks}")

            copied_cards = copied_decks = failed = 0
            for src in sorted(p for p in source_cards.rglob("*.json") if p.is_file()):
                try:
                    subdir = card_target_subdir(src, locations)
                    dest = cards_dir / subdir / src.name
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(src, dest)
                    copied_cards += 1
                except Exception as exc:  # noqa: BLE001
                    failed += 1
                    w(f"card copy FAILED {src.name}: {exc}")

            for src in sorted(p for p in source_decks.rglob("*.json") if p.is_file()):
                try:
                    rel = src.relative_to(source_decks)
                    dest = decks_dir / rel
                    dest.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(src, dest)
                    copied_decks += 1
                except Exception as exc:  # noqa: BLE001
                    failed += 1
                    w(f"deck copy FAILED {src.name}: {exc}")

            message = (
                f"cards copied {copied_cards}, decks copied {copied_decks}, "
                f"old card JSONs removed {removed_cards}, old deck JSONs removed {removed_decks}, failed {failed}"
            )
            w(f"done: {message}")

            # Persist the patch identity so train_bc.py can stamp every model trained from
            # now on with the card/deck patch its dataset was generated under.
            # patch_no auto-increments on every Download patch. The 7 pre-tracking models
            # are "patch 0" (no date), so the first dashboard-applied patch is patch 1.
            patch_marker_path = paths.ml_dir / "current_patch.json"
            prev_patch_no = 0
            if patch_marker_path.exists():
                try:
                    prev = json.loads(patch_marker_path.read_text(encoding="utf-8"))
                    if isinstance(prev, dict) and isinstance(prev.get("patch_no"), int):
                        prev_patch_no = prev["patch_no"]
                except (OSError, ValueError):
                    prev_patch_no = 0
            patch_no = prev_patch_no + 1
            patch_marker = {
                "patch_no": patch_no,
                "patch_ts": ts,
                "applied_unix_ms": int(time.time() * 1000),
                "source": hint,
                "cards_copied": copied_cards,
                "decks_copied": copied_decks,
            }
            patch_marker_path.write_text(json.dumps(patch_marker, indent=2), encoding="utf-8")
            w(f"patch marker: {patch_marker_path} (patch_no={patch_no}, patch_ts={ts})")

            sync_state["summary"] = {
                "ok": failed == 0,
                "mode": "json-patch",
                "message": message,
                "patch_no": patch_no,
                "patch_ts": ts,
                "cards_copied": copied_cards,
                "decks_copied": copied_decks,
                "removed_cards": removed_cards,
                "removed_decks": removed_decks,
                "failed": failed,
                "archives": archived,
                "pre_patch_stats": str(pre_patch_stats),
            }
        except Exception as exc:  # noqa: BLE001
            w(f"ERROR: {exc}")
            sync_state["summary"] = {"ok": False, "mode": "json-patch", "error": str(exc)}
        finally:
            sync_state["running"] = False

    def run_pre_patch_snapshot() -> None:
        handle = open_log("sync")

        def w(msg: str) -> None:
            handle.write(msg + "\n")
            handle.flush()

        try:
            w("# Create pre-patch meta snapshot only")
            source_root, hint = resolve_project_jsons_source()
            source_for_report = source_root if source_root is not None else Path(hint)
            w(f"source reference: {hint}")
            ts = timestamp_for_archive()
            pre_patch_stats = write_pre_patch_stats(ts, source_for_report, [])
            w(f"pre-patch stats: {pre_patch_stats}")
            w("done: snapshot created without changing Cards/ or Decks/")
            sync_state["summary"] = {
                "ok": True,
                "mode": "pre-patch-snapshot",
                "message": "snapshot created without changing Cards/ or Decks/",
                "pre_patch_stats": str(pre_patch_stats),
            }
        except Exception as exc:  # noqa: BLE001
            w(f"ERROR: {exc}")
            sync_state["summary"] = {"ok": False, "mode": "pre-patch-snapshot", "error": str(exc)}
        finally:
            sync_state["running"] = False

    def resolve_smb_source(attempt_mount: bool = False) -> tuple[Path | None, str]:
        """Return (local_path_to_share_subdir_or_None, human_readable_hint).

        Windows reaches the share through a UNC path directly (no mount). macOS mounts
        SMB shares under /Volumes/<share>; if it isn't mounted yet we optionally try a
        non-interactive `mount volume` (uses Keychain/guest credentials)."""
        if os.name == "nt":
            unc = "\\\\{}\\{}\\{}".format(SMB_HOST, SMB_SHARE, "\\".join(SMB_SUBPATH))
            return Path(unc), unc
        target = Path("/Volumes") / SMB_SHARE / Path(*SMB_SUBPATH)
        if not target.exists() and attempt_mount and sys.platform == "darwin":
            try:
                subprocess.run(
                    ["osascript", "-e", f'mount volume "smb://{SMB_HOST}/{SMB_SHARE}"'],
                    capture_output=True, timeout=40,
                )
            except Exception:
                pass
        return (target if target.exists() else None), str(target)

    def normalize_mirror_path(raw: str | None) -> tuple[Path | None, str]:
        """Resolve a user-supplied logs-copy path to its `Decisions` directory.

        The user may point at any level of a logs copy, so detect the layout instead of
        assuming a fixed depth. Accepts, most-specific first:
          * the `Decisions` folder itself
          * a `Logs Export/ML` folder (contains `Decisions/` + `games.jsonl`)
          * a `Logs Export` folder
          * a build/project root that contains `Logs Export/ML/Decisions`
          * any folder that directly holds `*_decisions.jsonl` files
        Returns (decisions_dir_or_None, resolved_or_attempted_hint)."""
        raw = (raw or "").strip()
        if not raw:
            return None, ""
        p = Path(raw).expanduser()
        if not p.exists():
            return None, str(p)
        if p.name == "Decisions":
            return p, str(p)
        for candidate in (
            p / "Decisions",
            p / "ML" / "Decisions",
            p / "Logs Export" / "ML" / "Decisions",
        ):
            if candidate.exists():
                return candidate, str(candidate)
        # Last resort: the folder itself directly holds decision logs.
        try:
            if next(p.glob("*_decisions.jsonl"), None) is not None:
                return p, str(p)
        except OSError:
            pass
        return None, str(p)

    def read_games_rows(path: Path) -> list[dict[str, Any]]:
        rows: list[dict[str, Any]] = []
        if not path.exists():
            return rows
        for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(row, dict) and row.get("game_id"):
                rows.append(row)
        return rows

    def merge_game_rows(base_rows: list[dict[str, Any]],
                        incoming_rows: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], int, int]:
        merged: list[dict[str, Any]] = [dict(row) for row in base_rows]
        by_id: dict[str, dict[str, Any]] = {}
        for row in merged:
            gid = row.get("game_id")
            if isinstance(gid, str) and gid:
                by_id.setdefault(gid, row)

        added = enriched = 0
        for incoming in incoming_rows:
            gid = incoming.get("game_id")
            if not isinstance(gid, str) or not gid:
                continue
            existing = by_id.get(gid)
            if existing is None:
                row = dict(incoming)
                merged.append(row)
                by_id[gid] = row
                added += 1
                continue

            changed = False
            for key, value in incoming.items():
                if key == "game_id" or value in (None, ""):
                    continue
                if existing.get(key) in (None, ""):
                    existing[key] = value
                    changed = True
            if changed:
                enriched += 1
        return merged, added, enriched

    def write_games_rows(path: Path, rows: list[dict[str, Any]]) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8") as handle:
            for row in rows:
                handle.write(json.dumps(row, ensure_ascii=False) + "\n")

    def merge_games_jsonl(local_path: Path, remote_path: Path,
                          bidirectional: bool = False) -> dict[str, int]:
        """Merge games.jsonl by game_id without deleting rows or overwriting existing values."""
        local_before = read_games_rows(local_path)
        remote_before = read_games_rows(remote_path)

        local_after, local_added, local_enriched = merge_game_rows(local_before, remote_before)
        if local_added or local_enriched:
            write_games_rows(local_path, local_after)

        remote_added = remote_enriched = 0
        if bidirectional:
            remote_after, remote_added, remote_enriched = merge_game_rows(remote_before, local_after)
            if remote_added or remote_enriched:
                write_games_rows(remote_path, remote_after)

        return {
            "local_rows": len(local_after),
            "remote_rows": len(remote_before),
            "local_added": local_added,
            "local_enriched": local_enriched,
            "remote_added": remote_added,
            "remote_enriched": remote_enriched,
        }

    def run_log_sync() -> None:
        import threading  # noqa: F401  (kept local like the curriculum watcher)
        handle = open_log("sync")

        def w(msg: str) -> None:
            handle.write(msg + "\n")
            handle.flush()

        try:
            w("# Fetch decision logs from the server (SMB)")
            src, hint = resolve_smb_source(attempt_mount=True)
            w(f"source: {hint}")
            # Server-collected logs (e.g. from the Pi5 log server) land in their own subfolder so they
            # stay a distinct training source instead of polluting the Decisions/ root or a game context.
            dest = paths.decisions_dir / "received"
            dest.mkdir(parents=True, exist_ok=True)
            w(f"destination: {dest}")
            if src is None or not src.exists():
                w("ERROR: source share is not reachable / not mounted.")
                if os.name == "nt":
                    w(f"Windows: make sure \\\\{SMB_HOST}\\{SMB_SHARE} is accessible (credentials).")
                else:
                    w(f"macOS: Finder → Go → Connect to Server → smb://{SMB_HOST}/{SMB_SHARE}, then retry.")
                sync_state["summary"] = {"ok": False, "error": "source unreachable"}
                return
            files = sorted(p for p in src.glob("*.jsonl") if p.is_file())
            w(f"found {len(files)} log file(s) on server")
            games_merged = {"local_added": 0, "local_enriched": 0, "remote_rows": 0}
            remote_games = src.parent / "games.jsonl"
            if remote_games.exists():
                try:
                    games_merged = merge_games_jsonl(paths.games_jsonl, remote_games, bidirectional=False)
                    w("games.jsonl merge: "
                      f"server_rows={games_merged['remote_rows']} "
                      f"added={games_merged['local_added']} "
                      f"enriched={games_merged['local_enriched']}")
                except Exception as exc:  # noqa: BLE001
                    w(f"games.jsonl merge WARNING: {exc}")
            else:
                w(f"games.jsonl merge skipped: {remote_games} not found")
            # Skip files we already have anywhere under Decisions/ (root or any subfolder), so a file
            # ingested before this 'received/' split isn't downloaded again into a second location.
            existing_names = {p.name for p in paths.decisions_dir.rglob("*.jsonl")}
            copied = skipped = failed = 0
            for i, f in enumerate(files, 1):
                # The receiver stores files as "<game_id>.jsonl"; the loader only discovers
                # "*_decisions.jsonl". Normalise the suffix on copy so received logs are trainable.
                target_name = f.name if f.name.endswith("_decisions.jsonl") else f.stem + "_decisions.jsonl"
                target = dest / target_name
                if target_name in existing_names or target.exists():
                    skipped += 1
                    continue
                try:
                    # copyfile copies BYTES ONLY. shutil.copy2 also runs copystat, which on
                    # macOS tries to replicate the SMB source's flags/xattrs onto the local
                    # file and fails with "[Errno 1] Operation not permitted". We don't need
                    # the source's permissions/timestamps for ingested logs.
                    shutil.copyfile(f, target)
                    copied += 1
                    w(f"[{i}/{len(files)}] copied {f.name} -> received/{target_name}")
                except Exception as exc:  # noqa: BLE001
                    failed += 1
                    w(f"[{i}/{len(files)}] FAILED {f.name}: {exc}")
            w(f"done: copied={copied} skipped(existing)={skipped} failed={failed}")
            # Fetched decision logs arrive without their games.jsonl, so winner metadata
            # would be missing and --winners-only would silently drop them. Reconstruct the
            # missing winner rows now so coverage self-heals after every sync.
            reconstructed = 0
            if copied > 0:
                try:
                    w("reconstructing missing winner rows (games.jsonl backfill)...")
                    stats = backfill_games_jsonl(paths.decisions_dir, paths.games_jsonl, apply=True)
                    reconstructed = stats["written"]
                    w(f"backfill: reconstructed {reconstructed} winner row(s) "
                      f"(usable A/B {stats['usable_ab']}, already {stats['already_in_games']})")
                except Exception as exc:  # noqa: BLE001
                    w(f"backfill WARNING: {exc}")
            try:
                meta = enrich_existing_metadata(paths.games_jsonl, paths.logs_root / "Deckbuilder", apply=True)
                if meta["written"]:
                    w(f"deck metadata enrichment: updated {meta['written']} existing row(s)")
            except Exception as exc:  # noqa: BLE001
                w(f"deck metadata enrichment WARNING: {exc}")
            sync_state["summary"] = {"ok": True, "copied": copied, "skipped": skipped,
                                     "failed": failed, "reconstructed": reconstructed,
                                     "games_added": games_merged["local_added"],
                                     "games_enriched": games_merged["local_enriched"]}
        except Exception as exc:  # noqa: BLE001
            w(f"ERROR: {exc}")
            sync_state["summary"] = {"ok": False, "error": str(exc)}
        finally:
            invalidate_decision_file_cache()
            sync_state["running"] = False

    def run_decisions_mirror_sync(mirror_dirs=None, direction="two-way") -> None:
        handle = open_log("sync")

        def w(msg: str) -> None:
            handle.write(msg + "\n")
            handle.flush()

        try:
            # direction selects which way decision files (and games.jsonl rows) flow:
            #   two-way → additive both ways (default, historical behavior)
            #   pull    → copy the chosen device's logs INTO local only (don't push local back)
            #   push    → copy local logs out to the device only (don't pull device into local)
            direction = (direction or "two-way").strip().lower()
            if direction not in ("two-way", "pull", "push"):
                direction = "two-way"
            include_upload = direction in ("two-way", "push")     # local → device
            include_download = direction in ("two-way", "pull")   # device → local
            dir_label = {
                "two-way": "two-way (additive)",
                "pull": "pull (device → local)",
                "push": "push (local → device)",
            }[direction]

            w("# Sync local Decisions ↔ logs-copy Decisions mirror(s)")
            w(f"direction: {dir_label}")
            local_root = paths.decisions_dir

            # Accept one or more logs-copy paths (e.g. one per device). The dashboard sends
            # a list; the env var may hold several separated by ';' or newlines.
            if mirror_dirs is None:
                env = (os.environ.get("TCG_DECISIONS_MIRROR_DIR")
                       or os.environ.get("TCG_MIRROR_DECISIONS_DIR") or "")
                raw_list = env.replace(";", "\n").splitlines()
            elif isinstance(mirror_dirs, str):
                raw_list = [mirror_dirs]
            else:
                raw_list = list(mirror_dirs)
            requested_list: list[str] = []
            seen_req: set[str] = set()
            for raw in raw_list:
                v = (raw or "").strip()
                if v and v not in seen_req:
                    seen_req.add(v)
                    requested_list.append(v)

            w(f"local:  {local_root}")
            if not requested_list:
                w("ERROR: no logs-copy path provided.")
                w("Enter the path(s) to the logs copy in the dashboard (or set TCG_DECISIONS_MIRROR_DIR).")
                w("Point at the build root, the 'Logs Export' or 'Logs Export/ML' folder, or the 'Decisions' folder itself.")
                sync_state["summary"] = {"ok": False, "error": "mirror path required"}
                return

            local_root.mkdir(parents=True, exist_ok=True)
            failed = 0

            # Resolve every requested path to its Decisions folder; skip the unresolvable ones.
            resolved: list[tuple[str, Path]] = []
            for req in requested_list:
                rr, hint = normalize_mirror_path(req)
                if rr is None:
                    failed += 1
                    w(f"ERROR: could not locate a Decisions folder under: {hint} (requested: {req})")
                    w("  Point at the build root, the 'Logs Export' or 'Logs Export/ML' folder, or the 'Decisions' folder itself.")
                    if sys.platform == "darwin":
                        w("  macOS: if it lives on a network drive, mount it first (Finder → Go → Connect to Server), then retry.")
                    continue
                resolved.append((req, rr))
            if not resolved:
                w("ERROR: none of the provided paths resolved to a Decisions folder.")
                sync_state["summary"] = {"ok": False, "error": "mirror Decisions folder not found"}
                return

            games_merged = {"local_added": 0, "local_enriched": 0, "remote_added": 0, "remote_enriched": 0}
            w("decision file copy preserves the relative folder under Decisions on both sides")

            uploaded = downloaded = skipped = 0

            def rel_map(root: Path) -> tuple[dict[str, Path], set[str]]:
                out: dict[str, Path] = {}
                skipped_roots: set[str] = set()
                failed_paths: list[Path] = []
                walk_errors: dict[Path, str] = {}

                def on_walk_error(exc: OSError) -> None:
                    failed_path = getattr(exc, "filename", None) or str(root)
                    failed = Path(failed_path)
                    failed_paths.append(failed)
                    walk_errors[failed] = str(exc)
                    try:
                        rel = str(failed.relative_to(root)).replace("\\", "/")
                        if rel and rel != ".":
                            skipped_roots.add(rel.rstrip("/") + "/")
                    except ValueError:
                        pass

                for dirpath, _dirnames, filenames in os.walk(root, onerror=on_walk_error):
                    for filename in filenames:
                        if not filename.endswith(".jsonl"):
                            continue
                        file = Path(dirpath) / filename
                        try:
                            rel = str(file.relative_to(root)).replace("\\", "/")
                        except ValueError:
                            continue
                        out.setdefault(rel, file)

                if os.name == "nt" and failed_paths:
                    for failed_path in failed_paths:
                        pattern = str(failed_path / "*.jsonl")
                        try:
                            result = subprocess.run(
                                ["cmd", "/c", "dir", "/s", "/b", pattern],
                                capture_output=True,
                                text=True,
                                timeout=180,
                            )
                        except Exception as exc:  # noqa: BLE001
                            w(f"scan fallback FAILED {failed_path}: {exc}")
                            continue
                        if result.returncode != 0:
                            detail = (result.stderr or result.stdout or "").strip()
                            walk_error = walk_errors.get(failed_path)
                            if walk_error:
                                w(f"scan WARNING skipped unreadable folder {failed_path}: {walk_error}")
                            w(f"scan fallback FAILED {failed_path}: {detail}")
                            continue
                        added = 0
                        for line in result.stdout.splitlines():
                            candidate = Path(line.strip())
                            if not candidate.name.endswith(".jsonl"):
                                continue
                            try:
                                rel = str(candidate.relative_to(root)).replace("\\", "/")
                            except ValueError:
                                continue
                            if rel not in out:
                                out[rel] = candidate
                                added += 1
                        if added:
                            try:
                                rel_root = str(failed_path.relative_to(root)).replace("\\", "/").rstrip("/") + "/"
                                skipped_roots.discard(rel_root)
                            except ValueError:
                                pass
                            w(f"scan fallback used cmd dir for {failed_path}: found {added} .jsonl file(s)")
                        else:
                            walk_error = walk_errors.get(failed_path)
                            if walk_error:
                                w(f"scan WARNING skipped unreadable folder {failed_path}: {walk_error}")
                return out, skipped_roots

            def under_skipped_root(rel: str, skipped_roots: set[str]) -> bool:
                return any(rel == skipped.rstrip("/") or rel.startswith(skipped) for skipped in skipped_roots)

            for idx, (req, remote_root) in enumerate(resolved, 1):
                w("")
                w(f"=== mirror {idx}/{len(resolved)}: {remote_root} ===")
                remote_root.mkdir(parents=True, exist_ok=True)

                remote_games = remote_root.parent / "games.jsonl"
                try:
                    if direction == "push":
                        # local → device only: merge local rows into the device's games.jsonl,
                        # leave local untouched. Remap keys so the summary reports it as uploaded.
                        raw = merge_games_jsonl(remote_games, paths.games_jsonl, bidirectional=False)
                        gm = {
                            "local_added": 0, "local_enriched": 0,
                            "remote_added": raw.get("local_added", 0),
                            "remote_enriched": raw.get("local_enriched", 0),
                        }
                    else:
                        # pull → device rows into local only; two-way → also push local rows back.
                        gm = merge_games_jsonl(paths.games_jsonl, remote_games,
                                               bidirectional=(direction == "two-way"))
                    for key in games_merged:
                        games_merged[key] += gm.get(key, 0)
                    w("games.jsonl merge: "
                      f"downloaded_rows={gm['local_added']} "
                      f"enriched_local={gm['local_enriched']} "
                      f"uploaded_rows={gm['remote_added']} "
                      f"enriched_remote={gm['remote_enriched']}")
                except Exception as exc:  # noqa: BLE001
                    failed += 1
                    w(f"games.jsonl merge FAILED: {exc}")

                local_files, local_skipped_roots = rel_map(local_root)
                remote_files, remote_skipped_roots = rel_map(remote_root)

                for rel, src in (sorted(local_files.items()) if include_upload else []):
                    if under_skipped_root(rel, remote_skipped_roots):
                        skipped += 1
                        continue
                    dest = remote_root / Path(*rel.split("/"))
                    if rel in remote_files or dest.exists():
                        skipped += 1
                        continue
                    try:
                        dest.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(src, dest)
                        uploaded += 1
                        w(f"upload {rel}")
                    except Exception as exc:  # noqa: BLE001
                        failed += 1
                        w(f"upload FAILED {rel}: {exc}")

                local_files, local_skipped_roots = rel_map(local_root)
                remote_files, remote_skipped_roots = rel_map(remote_root)
                for rel, src in (sorted(remote_files.items()) if include_download else []):
                    if under_skipped_root(rel, local_skipped_roots):
                        skipped += 1
                        continue
                    dest = local_root / Path(*rel.split("/"))
                    if rel in local_files or dest.exists():
                        skipped += 1
                        continue
                    try:
                        dest.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(src, dest)
                        downloaded += 1
                        w(f"download {rel}")
                    except Exception as exc:  # noqa: BLE001
                        failed += 1
                        w(f"download FAILED {rel}: {exc}")

            reconstructed = 0
            if downloaded > 0:
                try:
                    w("reconstructing missing winner rows (games.jsonl backfill)...")
                    stats = backfill_games_jsonl(paths.decisions_dir, paths.games_jsonl, apply=True)
                    reconstructed = stats["written"]
                    w(f"backfill: reconstructed {reconstructed} winner row(s) "
                      f"(usable A/B {stats['usable_ab']}, already {stats['already_in_games']})")
                except Exception as exc:  # noqa: BLE001
                    w(f"backfill WARNING: {exc}")
            try:
                meta = enrich_existing_metadata(paths.games_jsonl, paths.logs_root / "Deckbuilder", apply=True)
                if meta["written"]:
                    w(f"deck metadata enrichment: updated {meta['written']} existing row(s)")
            except Exception as exc:  # noqa: BLE001
                w(f"deck metadata enrichment WARNING: {exc}")

            message = (
                f"uploaded {uploaded}, downloaded {downloaded}, "
                f"games downloaded {games_merged['local_added']}, games uploaded {games_merged['remote_added']}, "
                f"skipped {skipped}, failed {failed}"
            )
            w(f"done: {message}")
            sync_state["summary"] = {
                "ok": True,
                "mode": "decisions-mirror",
                "direction": direction,
                "message": message,
                "mirrors": len(resolved),
                "uploaded": uploaded,
                "downloaded": downloaded,
                "skipped": skipped,
                "failed": failed,
                "reconstructed": reconstructed,
                "games_added": games_merged["local_added"],
                "games_enriched": games_merged["local_enriched"],
                "games_uploaded": games_merged["remote_added"],
                "remote_games_enriched": games_merged["remote_enriched"],
            }
        except Exception as exc:  # noqa: BLE001
            w(f"ERROR: {exc}")
            sync_state["summary"] = {"ok": False, "error": str(exc)}
        finally:
            invalidate_decision_file_cache()
            sync_state["running"] = False

    @app.post("/api/logs/sync")
    def logs_sync(payload: dict | None = None):
        if sync_state["running"]:
            return {"started": False, "status": "already-running"}
        import threading
        payload = payload or {}
        mode = str(payload.get("mode") or "server-fetch")
        if mode not in ("server-fetch", "decisions-mirror", "json-patch", "pre-patch-snapshot"):
            raise HTTPException(status_code=400, detail="unknown sync mode")
        sync_state["running"] = True
        sync_state["summary"] = None
        if mode == "json-patch":
            backup_logs = bool(payload.get("backup_logs"))
            target = lambda: run_json_patch_sync(backup_logs=backup_logs)
        elif mode == "pre-patch-snapshot":
            target = run_pre_patch_snapshot
        elif mode == "decisions-mirror":
            raw_dirs = payload.get("mirror_dirs")
            if raw_dirs is None:
                single = payload.get("mirror_dir")
                raw_dirs = [single] if single else []
            elif isinstance(raw_dirs, str):
                raw_dirs = [raw_dirs]
            mirror_dirs = [str(d).strip() for d in raw_dirs if str(d or "").strip()]
            direction = str(payload.get("direction") or "two-way").strip().lower()
            target = lambda: run_decisions_mirror_sync(mirror_dirs=mirror_dirs, direction=direction)
        else:
            target = run_log_sync
        threading.Thread(target=target, daemon=True).start()
        return {"started": True, "log": str(logs["sync"]) if logs["sync"] else None}

    def parse_metrics_file(path: Path) -> dict[str, Any]:
        import re
        pattern = re.compile(
            r"epoch=(\d+)\s+train_loss=([\d.]+)\s+train_acc=([\d.]+)\s+val_loss=([\d.]+)\s+val_acc=([\d.]+)"
        )
        run_pattern = re.compile(r'"run"\s*:\s*"([^"]+)"')
        model_path_pattern = re.compile(r'"model_path"\s*:\s*"([^"]+)"')
        from_model_pattern = re.compile(r"fine_tuning from=(.*?) input_dim=")
        epochs, train_loss, train_acc, val_loss, val_acc = [], [], [], [], []
        run_name = ""
        model_path = ""
        from_model = ""
        stage2_start_index: int | None = None
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            if "Stage 2 / 2" in line and stage2_start_index is None:
                stage2_start_index = len(epochs)
            m = pattern.search(line)
            if m:
                epochs.append(int(m.group(1)))
                train_loss.append(float(m.group(2)))
                train_acc.append(float(m.group(3)))
                val_loss.append(float(m.group(4)))
                val_acc.append(float(m.group(5)))
            run_match = run_pattern.search(line)
            if run_match:
                run_name = run_match.group(1)
            model_match = model_path_pattern.search(line)
            if model_match:
                model_path = model_match.group(1).replace("\\\\", "\\")
            from_model_match = from_model_pattern.search(line)
            if from_model_match:
                from_model = from_model_match.group(1).strip().replace("\\\\", "\\")
        model_exists = bool(model_path and Path(model_path).exists())
        return {
            "epochs": epochs,
            "train_loss": train_loss,
            "train_acc": train_acc,
            "val_loss": val_loss,
            "val_acc": val_acc,
            "run": run_name,
            "model_path": model_path,
            "model_exists": model_exists,
            "from_model": from_model,
            "stage2_start_index": stage2_start_index,
        }

    def parse_metrics_sidecar(json_path: Path) -> dict[str, Any] | None:
        """Read per-epoch curves from a model's `.json` sidecar (written by train_bc.py).

        This is the fallback source for the Metrics tile when a model arrived without its
        `train_*.log` — e.g. a model trained in another session/machine and synced over with
        only the `.pt` + `.json`. Returns None if the sidecar lacks usable epoch history.
        """
        try:
            meta = json.loads(json_path.read_text(encoding="utf-8", errors="replace"))
        except (OSError, ValueError):
            return None
        hist = meta.get("history") or {}
        epochs = hist.get("epochs") or []
        if not epochs:
            return None
        model_path = str(json_path.with_suffix(".pt"))
        return {
            "epochs": list(epochs),
            "train_loss": list(hist.get("train_loss") or []),
            "train_acc": list(hist.get("train_acc") or []),
            "val_loss": list(hist.get("val_loss") or []),
            "val_acc": list(hist.get("val_acc") or []),
            "run": str(meta.get("run") or json_path.stem),
            "model_path": model_path,
            "model_exists": Path(model_path).exists(),
            "from_model": str(meta.get("from_model") or ""),
            "stage2_start_index": None,
        }

    def resolve_model_path(raw: str | None) -> Path | None:
        """Resolve a stored from_model value from old/new runs to an existing .pt."""
        if not raw:
            return None
        candidate = Path(str(raw))
        options = [candidate]
        if not candidate.is_absolute():
            options.append(paths.ml_dir / candidate)
            options.append(paths.models_dir / candidate.name)
        else:
            options.append(paths.models_dir / candidate.name)
        for opt in options:
            try:
                if opt.exists():
                    return opt.resolve()
            except OSError:
                continue
        return None

    def metrics_for_model_path(model_path: Path) -> dict[str, Any] | None:
        """Find the metrics curve for a specific model, preferring its log over sidecar."""
        target_name = model_path.name
        for path in train_log_files():
            data = parse_metrics_file(path)
            if data["epochs"] and data["model_path"] and Path(data["model_path"]).name == target_name:
                return data
        return parse_metrics_sidecar(model_path.with_suffix(".json"))

    def normalize_epoch_axis(data: dict[str, Any]) -> dict[str, Any]:
        """Use a monotonic x-axis for visual continuity, regardless of per-stage epoch resets."""
        out = dict(data)
        out["epochs"] = list(range(1, len(data.get("epochs") or []) + 1))
        return out

    def stitched_metrics(data: dict[str, Any] | None) -> dict[str, Any] | None:
        """Display fine-tune runs as base curve + fine-tune curve without changing training semantics.

        Fine-tuning intentionally resets the optimizer and epoch counter. The dashboard stitches
        curves only for readability: weights continue from the base model, while the marker shows
        where the fine-tune phase begins.
        """
        if not data or not data.get("epochs"):
            return data
        # Two-stage logs already contain Stage 1 and Stage 2 in one train_*.log. Keep their data,
        # but make the x-axis monotonic and mark the handoff.
        stage2_start = data.get("stage2_start_index")
        if isinstance(stage2_start, int) and stage2_start > 0:
            out = normalize_epoch_axis(data)
            out["stage_boundary_index"] = stage2_start
            out["stage_boundary_label"] = "Stage 2 fine-tune starts"
            return out

        base_path = resolve_model_path(data.get("from_model"))
        if not base_path:
            return normalize_epoch_axis(data)
        base = metrics_for_model_path(base_path)
        if not base or not base.get("epochs"):
            out = normalize_epoch_axis(data)
            out["stage_boundary_index"] = None
            return out

        boundary = len(base["epochs"])
        out = dict(data)
        for key in ("train_loss", "train_acc", "val_loss", "val_acc"):
            out[key] = list(base.get(key) or []) + list(data.get(key) or [])
        out["epochs"] = list(range(1, len(out["val_acc"]) + 1))
        out["stage_boundary_index"] = boundary
        out["stage_boundary_label"] = "fine-tune starts"
        out["stitched_from"] = str(base_path)
        return out

    def train_log_files() -> list[Path]:
        if not paths.runs_dir.exists():
            return []
        return sorted(paths.runs_dir.glob("train_*.log"), key=lambda p: p.stat().st_mtime, reverse=True)

    def metrics_log_files(model_backed_only: bool = True) -> list[Path]:
        files = []
        for path in train_log_files():
            data = parse_metrics_file(path)
            if not data["epochs"]:
                continue
            if model_backed_only and data["model_path"] and not data["model_exists"]:
                continue
            files.append(path)
        return files

    def log_model_names() -> set[str]:
        """Basenames of model files already represented by a model-backed train log."""
        covered: set[str] = set()
        for path in train_log_files():
            data = parse_metrics_file(path)
            if data["epochs"] and data["model_path"]:
                covered.add(Path(data["model_path"]).name)
        return covered

    def model_sidecar_runs() -> list[tuple[str, Path, dict[str, Any]]]:
        """Models with sidecar epoch history but no train log on this machine.

        These are models synced in from another session/machine — the `.pt` + `.json` came
        across but the `train_*.log` (a server-session artifact) did not. Named `model:<stem>`
        so they stay distinct from log-backed runs and are rejected by the log-only delete-run.
        """
        if not paths.models_dir.exists():
            return []
        covered = log_model_names()
        runs: list[tuple[str, Path, dict[str, Any]]] = []
        for pt in sorted(paths.models_dir.glob("*.pt"), key=lambda p: p.stat().st_mtime, reverse=True):
            if pt.name in covered:
                continue
            side = parse_metrics_sidecar(pt.with_suffix(".json"))
            if side and side["epochs"]:
                runs.append(("model:" + pt.stem, pt, side))
        return runs

    def latest_model_metrics() -> dict[str, Any] | None:
        """Metrics for the latest model — full curves from its train log, else its sidecar."""
        latest = latest_model_path()
        if not latest:
            return None
        for path in train_log_files():
            data = parse_metrics_file(path)
            if data["epochs"] and data["model_path"] and Path(data["model_path"]).name == latest.name:
                return data
        return parse_metrics_sidecar(latest.with_suffix(".json"))

    @app.get("/api/metrics")
    def get_metrics():
        # "Show latest only" should reflect the latest *model*, even one synced over without
        # its train_*.log (sidecar fallback). Only if there is no model at all do we fall back
        # to the newest model-backed train log.
        data = latest_model_metrics()
        if data and data["epochs"]:
            return stitched_metrics(data)
        log_files = metrics_log_files()
        if not log_files:
            return {"epochs": [], "train_loss": [], "train_acc": [], "val_loss": [], "val_acc": []}
        return stitched_metrics(parse_metrics_file(log_files[0]))

    @app.get("/api/runs")
    def get_runs():
        runs = []
        for path in metrics_log_files():
            data = parse_metrics_file(path)
            if data["epochs"]:
                runs.append({
                    "name": path.stem,
                    "model_run": data["run"],
                    "model_path": data["model_path"],
                    "modified_unix_ms": int(path.stat().st_mtime * 1000),
                    "epochs": len(data["epochs"]),
                    "log_backed": True,
                })
        for name, pt, side in model_sidecar_runs():
            runs.append({
                "name": name,
                "model_run": side["run"],
                "model_path": side["model_path"],
                "modified_unix_ms": int(pt.stat().st_mtime * 1000),
                "epochs": len(side["epochs"]),
                "log_backed": False,
            })
        runs.sort(key=lambda r: r["modified_unix_ms"], reverse=True)
        return {"runs": runs}

    @app.get("/api/metrics/runs")
    def get_metrics_runs(names: str | None = None):
        wanted = [n for n in (names.split(",") if names else []) if n]
        files = metrics_log_files()
        by_log = {p.stem: p for p in files}
        by_sidecar = {name: (pt, data) for name, pt, data in model_sidecar_runs()}
        runs = []
        if wanted:
            order = wanted
        else:
            # Default comparison set: newest log-backed and sidecar runs together, capped.
            combined = [(p.stem, int(p.stat().st_mtime * 1000)) for p in files]
            combined += [(name, int(pt.stat().st_mtime * 1000)) for name, (pt, _d) in by_sidecar.items()]
            combined.sort(key=lambda x: x[1], reverse=True)
            order = [name for name, _ in combined[:8]]
        stitch_single = bool(wanted and len(order) == 1)
        for name in order:
            if name in by_log:
                path = by_log[name]
                data = parse_metrics_file(path)
                if data["epochs"]:
                    curve = stitched_metrics(data) if stitch_single else normalize_epoch_axis(data)
                    runs.append({"name": name, "modified_unix_ms": int(path.stat().st_mtime * 1000), **curve})
            elif name in by_sidecar:
                pt, data = by_sidecar[name]
                curve = stitched_metrics(data) if stitch_single else normalize_epoch_axis(data)
                runs.append({"name": name, "modified_unix_ms": int(pt.stat().st_mtime * 1000), **curve})
        return {"runs": runs}

    @app.post("/api/delete-run")
    def delete_run(payload: dict | None = None):
        payload = payload or {}
        name = str(payload.get("name") or "")
        if not name.startswith("train_"):
            raise HTTPException(status_code=400, detail="run name must start with train_")
        log_path = (paths.runs_dir / f"{name}.log").resolve()
        runs_root = paths.runs_dir.resolve()
        if runs_root not in log_path.parents or log_path.suffix != ".log" or not log_path.exists():
            raise HTTPException(status_code=404, detail="run log not found")
        data = parse_metrics_file(log_path)
        log_path.unlink()
        artifact_dir = None
        if data.get("run"):
            candidate = (paths.runs_dir / str(data["run"])).resolve()
            if runs_root in candidate.parents and candidate.is_dir():
                shutil.rmtree(candidate)
                artifact_dir = str(candidate)
        return {"deleted": True, "log": str(log_path), "artifact_dir": artifact_dir}

    @app.post("/predict")
    def predict(payload: dict):
        import torch

        try:
            if loaded_model["module"] is None:
                load_model()
            module = loaded_model["module"]
            device = loaded_model["device"]
            snapshot = payload.get("snapshot")
            legal_actions = payload.get("legal_actions") or payload.get("actions")
            if not isinstance(snapshot, dict):
                return {"error": "payload.snapshot must be an object"}
            if not isinstance(legal_actions, list) or not legal_actions:
                return {"error": "payload.legal_actions must be a non-empty list"}

            labels = []
            rows = []
            started = time.perf_counter()
            state = encoder.state_vector(snapshot)
            for index, action in enumerate(legal_actions):
                target_instance_id = -1
                if isinstance(action, str):
                    label = action
                    category = None
                elif isinstance(action, dict):
                    label = str(action.get("label") or action.get("Label") or action.get("type") or action.get("Type") or "")
                    category = action.get("category") or action.get("Category") or action.get("type") or action.get("Type")
                    target_instance_id = int(
                        action.get("target_instance_id", action.get("targetInstanceId", -1)) or -1
                    )
                else:
                    label = str(action)
                    category = None
                labels.append(label)
                action_vec = encoder.action_vector(
                    label,
                    category=category,
                    ordinal=index,
                    candidate_count=len(legal_actions),
                    target_instance_id=target_instance_id,
                    snapshot=snapshot,
                )
                rows.append(torch.as_tensor([*state, *action_vec], dtype=torch.float32, device=device))
            x = torch.stack(rows)
            with torch.no_grad():
                logits = module(x).view(-1)
                probs = torch.softmax(logits, dim=0)
            best = int(torch.argmax(probs).item())
            top = torch.topk(probs, k=min(3, len(labels)))
            elapsed_ms = (time.perf_counter() - started) * 1000.0
            return {
                "action_index": best,
                "action_label": labels[best],
                "confidence": float(probs[best].item()),
                "top3": [
                    {"action_index": int(i), "action_label": labels[int(i)], "confidence": float(v)}
                    for v, i in zip(top.values.tolist(), top.indices.tolist())
                ],
                "inference_ms": elapsed_ms,
                "model_path": str(loaded_model["path"]),
            }
        except Exception as exc:
            return {"error": str(exc)}

    # --- Replay ---------------------------------------------------------------

    def _target_position(snapshot: dict, target_instance_id: int) -> str:
        if target_instance_id < 0:
            return ""

        mine = snapshot.get("MyState") or {}
        active = mine.get("ActivePokemon") or {}
        if int(active.get("InstanceId", -1) or -1) == target_instance_id:
            return "ACTIVE"

        for index, pokemon in enumerate(mine.get("Bench") or []):
            if int((pokemon or {}).get("InstanceId", -1) or -1) == target_instance_id:
                return f"BENCH {index + 1}"

        for index, card in enumerate(mine.get("Hand") or []):
            if int((card or {}).get("InstanceId", -1) or -1) == target_instance_id:
                return f"HAND {index + 1}"

        return "TARGET"

    def _display_candidate_label(label: str, target_instance_id: int, snapshot: dict, duplicate_label: bool) -> str:
        if target_instance_id < 0:
            return label
        position = _target_position(snapshot, target_instance_id)
        suffix = f"{position} #{target_instance_id}" if position else f"#{target_instance_id}"
        return f"{label} [{suffix}]" if duplicate_label or position else f"{label} [#{target_instance_id}]"

    def _build_candidates(record: dict) -> list[dict]:
        """Mirror dataset.envelope_to_example: real scores + synthetic (skip)."""
        scores = [s for s in record.get("scores", []) if isinstance(s, dict)]
        label_counts: dict[str, int] = {}
        for score in scores:
            label = str(score.get("label") or "")
            label_counts[label] = label_counts.get(label, 0) + 1
        snapshot = record.get("snapshot") or {}
        candidates = [
            {
                "label": str(s.get("label") or ""),
                "display_label": _display_candidate_label(
                    str(s.get("label") or ""),
                    int(s.get("target_instance_id", -1) or -1),
                    snapshot,
                    label_counts.get(str(s.get("label") or ""), 0) > 1,
                ),
                "expert_score": int(s.get("score") or 0),
                "blocked": bool(s.get("blocked", False)),
                "reasons": list(s.get("reasons") or []),
                "target_instance_id": int(s.get("target_instance_id", -1) or -1),
            }
            for s in scores
        ]
        candidates.append(
            {"label": "(skip)", "display_label": "(skip)", "expert_score": 0, "blocked": False, "reasons": ["synthetic no-action candidate"], "target_instance_id": -1}
        )
        return candidates

    def _board_snapshot(snapshot: dict) -> dict:
        """Return only public board fields needed by the replay board renderer."""
        if not isinstance(snapshot, dict):
            return {}
        bench_size = configured_bench_size()

        def clean_pokemon(pokemon: Any) -> dict | None:
            if not isinstance(pokemon, dict):
                return None
            return {
                "InstanceId": pokemon.get("InstanceId"),
                "Name": pokemon.get("Name"),
                "CurrentHp": pokemon.get("CurrentHp"),
                "MaxHp": pokemon.get("MaxHp"),
                "PokemonType": pokemon.get("PokemonType"),
                "Stage": pokemon.get("Stage"),
                "RetreatCost": pokemon.get("RetreatCost"),
                "EnergyEquipped": pokemon.get("EnergyEquipped") or {},
                "IsPoisoned": pokemon.get("IsPoisoned"),
                "IsBurned": pokemon.get("IsBurned"),
                "SpecialCondition": pokemon.get("SpecialCondition"),
                "CanEvolve": pokemon.get("CanEvolve"),
                "Attacks": [
                    {
                        "Name": attack.get("Name"),
                        "Damage": attack.get("Damage"),
                        "EnergyCost": attack.get("EnergyCost") or [],
                    }
                    for attack in (pokemon.get("Attacks") or [])
                    if isinstance(attack, dict)
                ],
            }

        def clean_player(player: Any) -> dict:
            if not isinstance(player, dict):
                return {}
            return {
                "PlayerId": player.get("PlayerId"),
                "Score": player.get("Score"),
                "ActivePokemon": clean_pokemon(player.get("ActivePokemon")),
                "Bench": [p for p in (clean_pokemon(p) for p in (player.get("Bench") or [])) if p],
                "BenchSize": bench_size,
                "DeckCount": player.get("DeckCount"),
                "DiscardCount": player.get("DiscardCount"),
                "AvailableEnergy": player.get("AvailableEnergy"),
                "NextEnergy": player.get("NextEnergy"),
                "CanAddEnergy": player.get("CanAddEnergy"),
                "UsedSupporterThisTurn": player.get("UsedSupporterThisTurn"),
            }

        return {
            "TurnNumber": snapshot.get("TurnNumber"),
            "ActivePlayerId": snapshot.get("ActivePlayerId"),
            "MyState": clean_player(snapshot.get("MyState")),
            "OpponentState": clean_player(snapshot.get("OpponentState")),
        }

    def _model_probs(snapshot: dict, category: str, candidates: list[dict]) -> list[float] | None:
        """Run the loaded model over a decision's candidate set; None if unavailable."""
        if loaded_model["module"] is None:
            try:
                load_model()
            except Exception:
                return None
        import torch

        module = loaded_model["module"]
        device = loaded_model["device"]
        state = encoder.state_vector(snapshot)
        rows = []
        for index, cand in enumerate(candidates):
            action_vec = encoder.action_vector(
                label=cand["label"],
                category=category,
                ordinal=index,
                candidate_count=len(candidates),
                target_instance_id=cand["target_instance_id"],
                snapshot=snapshot,
            )
            rows.append(torch.as_tensor([*state, *action_vec], dtype=torch.float32, device=device))
        with torch.no_grad():
            logits = module(torch.stack(rows)).view(-1)
            probs = torch.softmax(logits, dim=0)
        return [float(p) for p in probs.tolist()]

    def replay_index_stamp() -> tuple[Any, ...]:
        game_meta_stamp = (0, 0)
        try:
            st = paths.games_jsonl.stat()
            game_meta_stamp = (st.st_size, st.st_mtime_ns)
        except OSError:
            pass
        return (*decision_logs_stamp(), *game_meta_stamp)

    REPLAY_GAME_LIMIT = 50

    def replay_index() -> dict[str, Any]:
        stamp = replay_index_stamp()
        if replay_index_cache.get("stamp") == stamp and replay_index_cache.get("data") is not None:
            return replay_index_cache["data"]

        games_meta = load_games(paths.games_jsonl)
        games = []
        game_paths: dict[str, Path] = {}
        indexed_files = decision_file_index()["files"]
        all_paths = sorted(
            indexed_files,
            key=lambda p: p.stem.replace("_decisions", ""),
            reverse=True,
        )
        for path in all_paths[:REPLAY_GAME_LIMIT]:
            game_id = None
            players: set[int] = set()
            for _, record in iter_jsonl(path):
                game_id = str(record.get("game_id") or path.stem.replace("_decisions", ""))
                if record.get("player_id") is not None:
                    players.add(int(record["player_id"]))
                break
            if game_id is None:
                game_id = path.stem.replace("_decisions", "")
            game_paths[game_id] = path
            meta = games_meta.get(game_id) or {}
            games.append(
                {
                    "game_id": game_id,
                    "file": path.name,
                    "modified_unix_ms": int(path.stat().st_mtime * 1000),
                    "decisions": None,
                    "usable_decisions": None,
                    "turns": meta.get("turns"),
                    "deck_a": meta.get("deck_a"),
                    "deck_b": meta.get("deck_b"),
                    "players": sorted(players),
                    "winner": meta.get("winner"),
                    "matchup": meta.get("matchup") or meta.get("decks"),
                }
            )
        games.sort(key=lambda g: g["game_id"], reverse=True)
        data = {"games": games, "game_paths": game_paths, "games_meta": games_meta, "total_games": len(all_paths), "limit": REPLAY_GAME_LIMIT}
        replay_index_cache.update({"stamp": stamp, "data": data})
        return data

    @app.get("/api/replay/games")
    def replay_games():
        index = replay_index()
        games = index["games"]
        return {"games": games, "count": len(games), "total_count": index.get("total_games", len(games)), "limit": index.get("limit", len(games)), "model_loaded": loaded_model["module"] is not None}

    @app.get("/api/replay/game/{game_id}")
    def replay_game(game_id: str):
        index = replay_index()
        path = index["game_paths"].get(game_id)
        if path is None:
            raise HTTPException(status_code=404, detail=f"No decision log for game '{game_id}'.")

        meta = index.get("games_meta", {}).get(game_id) or {}

        def _profile_label(brain: Any) -> str:
            if isinstance(brain, str) and brain.startswith("Algorithm:"):
                return brain.split(":", 1)[1] or "—"
            return brain if isinstance(brain, str) and brain else "—"

        steps = []
        agreements = 0
        scored = 0
        by_category: dict[str, dict[str, int]] = {}
        for _, record in iter_jsonl(path):
            if str(record.get("game_id") or "") != game_id:
                continue
            ok, reason = usable_decision(record)
            category = str(record.get("category") or "Unknown")
            chosen_label = str(record.get("chosen_label") or "")
            step = {
                "seq": int(record.get("seq") or 0),
                "turn": int(record.get("turn") or 0),
                "player_id": record.get("player_id"),
                "category": category,
                "chosen_label": chosen_label,
                "usable": ok,
                "reason": reason,
                "candidates": [],
                "expert_index": None,
                "model_index": None,
                "model_confidence": None,
                "agree": None,
                "exact_agree": None,
                "snapshot": _board_snapshot(record.get("snapshot") or {}),
            }
            if ok:
                candidates = _build_candidates(record)
                chosen_target = int(record.get("chosen_target_instance_id", -1) or -1)
                expert_index = choose_label_index(candidates, chosen_label, chosen_target)
                probs = _model_probs(record.get("snapshot") or {}, category, candidates)
                model_index = max(range(len(probs)), key=lambda i: probs[i]) if probs else None
                for i, cand in enumerate(candidates):
                    cand["model_prob"] = probs[i] if probs else None
                    cand["is_expert"] = i == expert_index
                    cand["is_model"] = i == model_index
                step["candidates"] = candidates
                step["expert_index"] = expert_index
                step["model_index"] = model_index
                step["model_confidence"] = probs[model_index] if (probs and model_index is not None) else None
                if probs is not None and expert_index is not None and model_index is not None:
                    exact_agree = expert_index == model_index
                    agree = exact_agree or candidates_equivalent(candidates[expert_index], candidates[model_index], category)
                    step["agree"] = agree
                    step["exact_agree"] = exact_agree
                    scored += 1
                    agreements += int(agree)
                    bucket = by_category.setdefault(category, {"scored": 0, "agree": 0})
                    bucket["scored"] += 1
                    bucket["agree"] += int(agree)
            steps.append(step)

        steps.sort(key=lambda s: s["seq"])
        return {
            "game_id": game_id,
            "file": path.name,
            "steps": steps,
            "summary": {
                "total_steps": len(steps),
                "scored": scored,
                "agreements": agreements,
                "agreement_rate": (agreements / scored) if scored else None,
                "by_category": by_category,
                "model_loaded": loaded_model["module"] is not None,
                "model_path": str(loaded_model["path"]) if loaded_model["path"] else None,
                "deck_a": meta.get("deck_a"),
                "deck_b": meta.get("deck_b"),
                "profile_a": _profile_label(meta.get("brain_a")),
                "profile_b": _profile_label(meta.get("brain_b")),
            },
        }

    return app


def main() -> int:
    parser = argparse.ArgumentParser(description="Run the TCG Station ML web dashboard/API.")
    add_path_args(parser)
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8000)
    args = parser.parse_args()

    import uvicorn

    uvicorn.run(
        create_app(
            args.root,
            ml_root=args.ml_root,
            build_root=args.build_root,
            logs_dir=args.logs_dir,
            cards_dir=args.cards_dir,
            decks_dir=args.decks_dir,
        ),
        host=args.host,
        port=args.port,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
