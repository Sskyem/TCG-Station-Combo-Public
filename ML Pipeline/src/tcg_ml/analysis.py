"""Exploratory analysis of benchmark game results (games.jsonl).

Self-contained statistics implemented on the Python standard library only
(no numpy/scipy/pandas), so the dashboard can render an "Analysis" tab without
extra dependencies. The techniques mirror the data-visualisation course toolkit:
descriptive statistics / EDA, distribution shape (skewness, kurtosis, outliers),
a normality test (Jarque-Bera), correlation matrices (Pearson + Spearman) and
hypothesis testing (binomial test for first-player advantage, chi-square test of
independence for win-rate vs. algorithm profile).
"""

from __future__ import annotations

import math
from typing import Any, Iterable, Sequence

Number = float


# --------------------------------------------------------------------------- #
# Descriptive statistics (EDA)
# --------------------------------------------------------------------------- #
def _quantile(sorted_vals: Sequence[float], q: float) -> float:
    """Linear-interpolation quantile (same convention as numpy 'linear')."""
    n = len(sorted_vals)
    if n == 0:
        return float("nan")
    if n == 1:
        return float(sorted_vals[0])
    pos = q * (n - 1)
    lo = int(math.floor(pos))
    hi = int(math.ceil(pos))
    if lo == hi:
        return float(sorted_vals[lo])
    frac = pos - lo
    return float(sorted_vals[lo]) * (1 - frac) + float(sorted_vals[hi]) * frac


def describe(values: Iterable[float]) -> dict[str, Any]:
    """Count, central tendency, spread and distribution shape for one variable."""
    xs = [float(v) for v in values if v is not None and not _is_nan(v)]
    n = len(xs)
    if n == 0:
        return {"n": 0}
    s = sorted(xs)
    mean = sum(xs) / n
    # Sample variance / std (ddof=1) when we have at least two observations.
    if n > 1:
        var = sum((x - mean) ** 2 for x in xs) / (n - 1)
    else:
        var = 0.0
    std = math.sqrt(var)
    q1 = _quantile(s, 0.25)
    med = _quantile(s, 0.50)
    q3 = _quantile(s, 0.75)
    return {
        "n": n,
        "mean": mean,
        "std": std,
        "min": s[0],
        "q1": q1,
        "median": med,
        "q3": q3,
        "max": s[-1],
        "iqr": q3 - q1,
        "skewness": _skewness(xs, mean, std),
        "kurtosis": _excess_kurtosis(xs, mean, std),
    }


def _skewness(xs: Sequence[float], mean: float, std: float) -> float | None:
    n = len(xs)
    if n < 3 or std == 0:
        return None
    # Population third moment standardised (Fisher-Pearson g1).
    m3 = sum((x - mean) ** 3 for x in xs) / n
    sigma = math.sqrt(sum((x - mean) ** 2 for x in xs) / n)
    if sigma == 0:
        return None
    return m3 / (sigma ** 3)


def _excess_kurtosis(xs: Sequence[float], mean: float, std: float) -> float | None:
    n = len(xs)
    if n < 4 or std == 0:
        return None
    m4 = sum((x - mean) ** 4 for x in xs) / n
    sigma2 = sum((x - mean) ** 2 for x in xs) / n
    if sigma2 == 0:
        return None
    return m4 / (sigma2 ** 2) - 3.0  # excess (normal -> 0)


def histogram(values: Iterable[float], bins: int = 12) -> dict[str, Any]:
    xs = [float(v) for v in values if v is not None and not _is_nan(v)]
    if not xs:
        return {"edges": [], "counts": [], "bin_width": 0.0}
    lo, hi = min(xs), max(xs)
    if lo == hi:
        return {"edges": [lo, lo + 1.0], "counts": [len(xs)], "bin_width": 1.0}
    bins = max(1, int(bins))
    width = (hi - lo) / bins
    counts = [0] * bins
    for x in xs:
        idx = int((x - lo) / width)
        if idx >= bins:
            idx = bins - 1
        counts[idx] += 1
    edges = [lo + i * width for i in range(bins + 1)]
    return {"edges": edges, "counts": counts, "bin_width": width}


def iqr_outliers(values: Iterable[float]) -> dict[str, Any]:
    """Tukey fences: values outside [Q1 - 1.5*IQR, Q3 + 1.5*IQR]."""
    xs = [float(v) for v in values if v is not None and not _is_nan(v)]
    if len(xs) < 4:
        return {"lower_fence": None, "upper_fence": None, "outliers": [], "count": 0}
    s = sorted(xs)
    q1 = _quantile(s, 0.25)
    q3 = _quantile(s, 0.75)
    iqr = q3 - q1
    lo = q1 - 1.5 * iqr
    hi = q3 + 1.5 * iqr
    out = [x for x in xs if x < lo or x > hi]
    return {"lower_fence": lo, "upper_fence": hi, "outliers": sorted(out), "count": len(out)}


# --------------------------------------------------------------------------- #
# Correlation
# --------------------------------------------------------------------------- #
def pearson(x: Sequence[float], y: Sequence[float]) -> float | None:
    n = len(x)
    if n != len(y) or n < 2:
        return None
    mx = sum(x) / n
    my = sum(y) / n
    sxy = sum((a - mx) * (b - my) for a, b in zip(x, y))
    sxx = sum((a - mx) ** 2 for a in x)
    syy = sum((b - my) ** 2 for b in y)
    if sxx == 0 or syy == 0:
        return None
    return sxy / math.sqrt(sxx * syy)


def _rankdata(values: Sequence[float]) -> list[float]:
    """Average ranks (1-based), ties share the mean of their rank span."""
    order = sorted(range(len(values)), key=lambda i: values[i])
    ranks = [0.0] * len(values)
    i = 0
    while i < len(order):
        j = i
        while j + 1 < len(order) and values[order[j + 1]] == values[order[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0  # 1-based average rank
        for k in range(i, j + 1):
            ranks[order[k]] = avg
        i = j + 1
    return ranks


def spearman(x: Sequence[float], y: Sequence[float]) -> float | None:
    if len(x) != len(y) or len(x) < 2:
        return None
    return pearson(_rankdata(x), _rankdata(y))


def correlation_matrix(columns: dict[str, Sequence[float]], method: str = "pearson") -> dict[str, Any]:
    keys = list(columns.keys())
    fn = pearson if method == "pearson" else spearman
    matrix: list[list[float | None]] = []
    for a in keys:
        row: list[float | None] = []
        for b in keys:
            row.append(fn(columns[a], columns[b]))
        matrix.append(row)
    return {"labels": keys, "matrix": matrix, "method": method}


# --------------------------------------------------------------------------- #
# Distribution tail / special functions (pure-python p-values)
# --------------------------------------------------------------------------- #
def _is_nan(v: Any) -> bool:
    try:
        return isinstance(v, float) and math.isnan(v)
    except Exception:
        return False


def _gammq(s: float, x: float) -> float:
    """Regularised upper incomplete gamma Q(s, x) = 1 - P(s, x)."""
    if x < 0 or s <= 0:
        return float("nan")
    if x == 0:
        return 1.0
    if x < s + 1.0:
        return 1.0 - _gser(s, x)
    return _gcf(s, x)


def _gser(s: float, x: float) -> float:
    """Series representation of the lower regularised incomplete gamma P(s, x)."""
    gln = math.lgamma(s)
    ap = s
    total = 1.0 / s
    delta = total
    for _ in range(500):
        ap += 1.0
        delta *= x / ap
        total += delta
        if abs(delta) < abs(total) * 1e-12:
            break
    return total * math.exp(-x + s * math.log(x) - gln)


def _gcf(s: float, x: float) -> float:
    """Continued-fraction representation of the upper regularised gamma Q(s, x)."""
    gln = math.lgamma(s)
    tiny = 1e-300
    b = x + 1.0 - s
    c = 1.0 / tiny
    d = 1.0 / b
    h = d
    for i in range(1, 500):
        an = -i * (i - s)
        b += 2.0
        d = an * d + b
        if abs(d) < tiny:
            d = tiny
        c = b + an / c
        if abs(c) < tiny:
            c = tiny
        d = 1.0 / d
        delta = d * c
        h *= delta
        if abs(delta - 1.0) < 1e-12:
            break
    return math.exp(-x + s * math.log(x) - gln) * h


def chi2_sf(x: float, df: int) -> float:
    """Survival function (upper tail p-value) of the chi-square distribution."""
    if x <= 0:
        return 1.0
    if df <= 0:
        return float("nan")
    return _gammq(df / 2.0, x / 2.0)


# --------------------------------------------------------------------------- #
# Hypothesis tests
# --------------------------------------------------------------------------- #
def jarque_bera(values: Iterable[float]) -> dict[str, Any]:
    """Jarque-Bera normality test. H0: data is normally distributed."""
    xs = [float(v) for v in values if v is not None and not _is_nan(v)]
    n = len(xs)
    if n < 8:
        return {"n": n, "statistic": None, "p_value": None,
                "normal": None, "note": "need >= 8 samples"}
    mean = sum(xs) / n
    sigma = math.sqrt(sum((x - mean) ** 2 for x in xs) / n)
    if sigma == 0:
        return {"n": n, "statistic": None, "p_value": None,
                "normal": None, "note": "zero variance"}
    skew = sum((x - mean) ** 3 for x in xs) / n / sigma ** 3
    kurt = sum((x - mean) ** 4 for x in xs) / n / sigma ** 4  # raw (normal -> 3)
    jb = n / 6.0 * (skew ** 2 + (kurt - 3.0) ** 2 / 4.0)
    p = chi2_sf(jb, 2)  # JB ~ chi2 with df=2
    return {"n": n, "statistic": jb, "p_value": p, "skewness": skew,
            "excess_kurtosis": kurt - 3.0, "normal": (p is not None and p >= 0.05)}


def _binom_cdf_pmf(k: int, n: int, p: float) -> float:
    return math.exp(math.lgamma(n + 1) - math.lgamma(k + 1) - math.lgamma(n - k + 1)
                    + k * math.log(p) + (n - k) * math.log(1 - p))


def binom_test_two_sided(k: int, n: int, p: float = 0.5) -> float:
    """Exact two-sided binomial test p-value (sum of outcomes no more likely than k)."""
    if n == 0:
        return float("nan")
    if p <= 0 or p >= 1:
        return float("nan")
    obs = _binom_cdf_pmf(k, n, p)
    tol = obs * (1 + 1e-9)
    total = 0.0
    for i in range(n + 1):
        if _binom_cdf_pmf(i, n, p) <= tol:
            total += _binom_cdf_pmf(i, n, p)
    return min(1.0, total)


def chi_square_independence(table: list[list[int]]) -> dict[str, Any]:
    """Pearson chi-square test of independence on an R x C contingency table."""
    rows = len(table)
    cols = len(table[0]) if rows else 0
    if rows < 2 or cols < 2:
        return {"chi2": None, "df": None, "p_value": None, "note": "need >= 2x2"}
    row_tot = [sum(r) for r in table]
    col_tot = [sum(table[r][c] for r in range(rows)) for c in range(cols)]
    grand = sum(row_tot)
    if grand == 0:
        return {"chi2": None, "df": None, "p_value": None, "note": "empty table"}
    chi2 = 0.0
    for r in range(rows):
        for c in range(cols):
            exp = row_tot[r] * col_tot[c] / grand
            if exp > 0:
                chi2 += (table[r][c] - exp) ** 2 / exp
    df = (rows - 1) * (cols - 1)
    return {"chi2": chi2, "df": df, "p_value": chi2_sf(chi2, df)}


# --------------------------------------------------------------------------- #
# Top-level: analyse games.jsonl records
# --------------------------------------------------------------------------- #
def _profile_of(brain: Any) -> str | None:
    if not isinstance(brain, str):
        return None
    if brain.startswith("Algorithm:"):
        return brain.split(":", 1)[1] or None
    return None


def _player_type(brain: Any) -> str:
    """Player type from a brain label: "Algorithm:Ramp" -> "Algorithm", "Human" -> "Human",
    missing/unknown -> "?"."""
    if not isinstance(brain, str) or not brain:
        return "?"
    return brain.split(":", 1)[0]


def matchup_key(game: dict[str, Any]) -> str:
    """Order-independent player-type matchup label, e.g. "Algorithm vs Human" (so A-vs-B and
    B-vs-A collapse to one bucket)."""
    a, b = _player_type(game.get("brain_a")), _player_type(game.get("brain_b"))
    return " vs ".join(sorted([a, b]))


def analyze_games(games: Iterable[dict[str, Any]], source: str = "all", matchup: str = "all") -> dict[str, Any]:
    """Run the full EDA + hypothesis-test suite over benchmark game records.

    ``source`` filters by the games.jsonl ``is_benchmark`` flag: ``all`` keeps everything,
    ``benchmark`` keeps only flagged benchmark games, ``interactive`` keeps only non-benchmark
    games. Records that predate the flag (missing ``is_benchmark``) are treated as unknown and
    are excluded from both the ``benchmark`` and ``interactive`` views.

    ``matchup`` filters by the order-independent player-type matchup (e.g. "Algorithm vs Human");
    ``all`` keeps every matchup. The list of matchups available in the current ``source`` view is
    returned as ``matchups`` so the UI can offer them.
    """
    rows = [g for g in games if isinstance(g, dict)]
    n_before_source = len(rows)
    if source == "benchmark":
        rows = [g for g in rows if g.get("is_benchmark") is True]
    elif source == "interactive":
        rows = [g for g in rows if g.get("is_benchmark") is False]
    n_excluded_by_source = n_before_source - len(rows)

    # Matchups available in the current source view (for the UI dropdown), counted before the
    # matchup filter narrows the set. Known matchups first, the all-unknown "? vs ?" bucket last.
    matchup_counts: dict[str, int] = {}
    for g in rows:
        k = matchup_key(g)
        matchup_counts[k] = matchup_counts.get(k, 0) + 1
    available_matchups = sorted(
        ({"key": k, "games": c} for k, c in matchup_counts.items()),
        key=lambda m: (m["key"] == "? vs ?", "?" in m["key"], -m["games"], m["key"]),
    )

    n_before_matchup = len(rows)
    if matchup and matchup != "all":
        rows = [g for g in rows if matchup_key(g) == matchup]
    n_excluded_by_matchup = n_before_matchup - len(rows)

    n = len(rows)
    if n == 0:
        return {
            "ok": False,
            "reason": "no games" if (source == "all" and matchup == "all") else "no games match the filters",
            "n_games": 0,
            "source": source,
            "matchup": matchup,
            "matchups": available_matchups,
            "n_excluded_by_source": n_excluded_by_source,
            "n_excluded_by_matchup": n_excluded_by_matchup,
        }

    turns = [float(g["turns"]) for g in rows if isinstance(g.get("turns"), (int, float))]
    durations = [float(g["duration_ms"]) / 1000.0 for g in rows
                 if isinstance(g.get("duration_ms"), (int, float))]
    margins, totals, score_a, score_b = [], [], [], []
    for g in rows:
        a, b = g.get("score_a"), g.get("score_b")
        if isinstance(a, (int, float)) and isinstance(b, (int, float)):
            score_a.append(float(a))
            score_b.append(float(b))
            margins.append(abs(float(a) - float(b)))
            totals.append(float(a) + float(b))

    # Cards drawn per player per game (each game contributes up to two values). Lets us see how the
    # number of cards drawn relates to game length / KO points and whether players approach deck size.
    cards_drawn: list[float] = []
    for g in rows:
        for key in ("cards_drawn_a", "cards_drawn_b"):
            v = g.get(key)
            if isinstance(v, (int, float)):
                cards_drawn.append(float(v))

    # --- descriptive statistics table ---
    descriptive = {
        "turns": describe(turns),
        "duration_s": describe(durations),
        "score_margin": describe(margins),
        "score_total": describe(totals),
    }
    if cards_drawn:
        descriptive["cards_drawn"] = describe(cards_drawn)

    # --- distribution of game length ---
    hist_turns = histogram(turns, bins=12)
    outliers_turns = iqr_outliers(turns)
    normality_turns = jarque_bera(turns)

    # --- correlation across numeric features (aligned per-game) ---
    aligned = [g for g in rows
               if isinstance(g.get("turns"), (int, float))
               and isinstance(g.get("score_a"), (int, float))
               and isinstance(g.get("score_b"), (int, float))]
    # Correlation columns are deliberately limited to mutually independent quantities. score_a and
    # score_b are excluded because margin = |score_a - score_b| would make their correlation with
    # margin tautological; margin (|a-b|) and total (a+b) are independent transforms, so both stay.
    # duration_s is wall-clock (hardware / LLM latency), not a gameplay feature, so it is kept out of
    # the matrix (it still appears in the descriptive EDA table with a caveat).
    cols = {
        "turns": [float(g["turns"]) for g in aligned],
        "margin": [abs(float(g["score_a"]) - float(g["score_b"])) for g in aligned],
        "total": [float(g["score_a"]) + float(g["score_b"]) for g in aligned],
    }
    # cards_drawn columns only appear once every aligned game carries them (older logs predate the
    # field), mirroring the duration_s gate so a partial dataset never misaligns the matrix.
    has_cards = bool(aligned) and all(
        isinstance(g.get("cards_drawn_a"), (int, float))
        and isinstance(g.get("cards_drawn_b"), (int, float))
        for g in aligned)
    if has_cards:
        cols["cards_drawn_a"] = [float(g["cards_drawn_a"]) for g in aligned]
        cols["cards_drawn_b"] = [float(g["cards_drawn_b"]) for g in aligned]
    pearson_m = correlation_matrix(cols, "pearson") if len(aligned) >= 2 else None
    spearman_m = correlation_matrix(cols, "spearman") if len(aligned) >= 2 else None

    # --- hypothesis test 1: first-player (seat A) advantage ---
    decided = [g for g in rows if g.get("winner") in ("A", "B")]
    a_wins = sum(1 for g in decided if g.get("winner") == "A")
    nd = len(decided)
    fp = {
        "a_wins": a_wins,
        "b_wins": nd - a_wins,
        "n": nd,
        "a_win_rate": (a_wins / nd) if nd else None,
        "p_value": binom_test_two_sided(a_wins, nd, 0.5) if nd else None,
    }
    fp["significant"] = (fp["p_value"] is not None and fp["p_value"] < 0.05)

    # --- hypothesis test 2: win-rate vs. algorithm profile (heterogeneous games) ---
    profile_stats: dict[str, dict[str, int]] = {}
    for g in decided:
        pa, pb = _profile_of(g.get("brain_a")), _profile_of(g.get("brain_b"))
        if not pa or not pb or pa == pb:
            continue
        winner_profile = pa if g.get("winner") == "A" else pb
        loser_profile = pb if g.get("winner") == "A" else pa
        profile_stats.setdefault(winner_profile, {"wins": 0, "losses": 0})["wins"] += 1
        profile_stats.setdefault(loser_profile, {"wins": 0, "losses": 0})["losses"] += 1
    profile_block: dict[str, Any] = {"profiles": [], "test": None}
    if profile_stats:
        labels = sorted(profile_stats.keys())
        table = [[profile_stats[p]["wins"], profile_stats[p]["losses"]] for p in labels]
        for p in labels:
            w = profile_stats[p]["wins"]
            tot = w + profile_stats[p]["losses"]
            profile_block["profiles"].append({
                "profile": p, "wins": w, "losses": profile_stats[p]["losses"],
                "games": tot, "win_rate": (w / tot) if tot else None,
            })
        if len(labels) >= 2:
            profile_block["test"] = chi_square_independence(table)

    # --- standings: per (deck, profile) win rate, like the benchmark "Standings (by win rate)" log ---
    deck_stats: dict[tuple[str, str], dict[str, int]] = {}

    def _bump(deck: Any, profile: str, result: str) -> None:
        if not isinstance(deck, str) or not deck:
            return
        s = deck_stats.setdefault((deck, profile), {"wins": 0, "losses": 0, "draws": 0})
        s[result] += 1

    for g in rows:
        w = g.get("winner")
        pa = _profile_of(g.get("brain_a")) or "Standard"
        pb = _profile_of(g.get("brain_b")) or "Standard"
        if w == "A":
            _bump(g.get("deck_a"), pa, "wins")
            _bump(g.get("deck_b"), pb, "losses")
        elif w == "B":
            _bump(g.get("deck_a"), pa, "losses")
            _bump(g.get("deck_b"), pb, "wins")
        elif w == "Draw":
            _bump(g.get("deck_a"), pa, "draws")
            _bump(g.get("deck_b"), pb, "draws")

    deck_standings = []
    for (deck, profile), s in deck_stats.items():
        games = s["wins"] + s["losses"] + s["draws"]
        deck_standings.append({
            "deck": deck, "profile": profile,
            "wins": s["wins"], "losses": s["losses"], "draws": s["draws"],
            "games": games,
            "win_rate": (s["wins"] / games) if games else None,
        })
    deck_standings.sort(
        key=lambda r: (r["win_rate"] if r["win_rate"] is not None else -1.0, r["games"]),
        reverse=True)

    # --- end-reason breakdown (data storytelling) ---
    reasons: dict[str, int] = {}
    for g in rows:
        r = g.get("end_reason") or "unknown"
        reasons[r] = reasons.get(r, 0) + 1

    deck_attributed = sum(1 for g in rows if g.get("deck_a") and g.get("deck_b"))
    profile_attributed = sum(1 for g in rows if g.get("brain_a") and g.get("brain_b"))

    return {
        "ok": True,
        "n_games": n,
        "n_decided": nd,
        "n_attributed": profile_attributed,
        "n_deck_attributed": deck_attributed,
        "n_profile_attributed": profile_attributed,
        "source": source,
        "matchup": matchup,
        "matchups": available_matchups,
        "n_excluded_by_source": n_excluded_by_source,
        "n_excluded_by_matchup": n_excluded_by_matchup,
        "descriptive": descriptive,
        "histogram_turns": hist_turns,
        "outliers_turns": outliers_turns,
        "normality_turns": normality_turns,
        "correlation_pearson": pearson_m,
        "correlation_spearman": spearman_m,
        "first_player": fp,
        "profile_winrate": profile_block,
        "deck_winrate": deck_standings,
        "end_reasons": reasons,
    }
