#!/usr/bin/env python3
"""Mine the wild-magic (and optionally dialogue) audit logs for the buckets that drive
capability work and latency tuning (docs/OPTIMIZATION_PLAN.md WS0.2).

Usage:
    python scripts/mine_wild_magic_audit.py [logs/wild_magic_audit.jsonl] [--top N]
    python scripts/mine_wild_magic_audit.py --dialogue logs/dialogue_audit.jsonl

Buckets reported for wild magic:
  * intentional rejections, grouped by rejectedReason
  * technical failures, grouped by validation code (unsupported_effect, operation_shape,
    promise_effect_missing, ...)
  * casts that routed zero capabilities (candidates for new trigger words / cards)
  * "boring" casts whose only effects were `message` (the boring-magic smell)

If audit records carry providerStats (added WS0.1), a latency table prints per-purpose
promptTokens / loadMs / totalMs percentiles. Older records simply lack the field and are skipped
from that table.
"""
from __future__ import annotations

import argparse
import collections
import json
import statistics
import sys


def load(path):
    rows = []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def pct(values, p):
    if not values:
        return 0.0
    ordered = sorted(values)
    index = min(len(ordered) - 1, int(len(ordered) * p))
    return ordered[index]


def top(counter, n):
    return counter.most_common(n)


def show_bucket(title, examples, n):
    print(f"\n== {title} ({len(examples)}) ==")
    for text, count in top(collections.Counter(examples), n):
        print(f"  {count:4d}  {text}")


def provider_stats_rows(rows, purpose_of):
    by_purpose = collections.defaultdict(lambda: collections.defaultdict(list))
    for row in rows:
        stats = purpose_of(row)
        if not stats:
            continue
        purpose, s = stats
        for key in ("promptTokens", "outputTokens", "loadMs", "promptEvalMs", "generationMs", "totalMs"):
            value = s.get(key)
            if isinstance(value, (int, float)):
                by_purpose[purpose][key].append(value)
    return by_purpose


def print_latency(by_purpose):
    if not by_purpose:
        print("\n(no providerStats found — re-run after a live session with WS0.1 telemetry)")
        return
    print("\n== provider stats (p50 / p95) ==")
    header = f"  {'purpose':22s} {'n':>5s} {'promptTok':>18s} {'genTok':>14s} {'loadMs':>14s} {'totalMs':>16s}"
    print(header)
    for purpose, fields in sorted(by_purpose.items()):
        n = len(fields.get("promptTokens") or fields.get("totalMs") or [])

        def cell(key):
            vals = fields.get(key) or []
            return f"{pct(vals, 0.5):.0f}/{pct(vals, 0.95):.0f}" if vals else "-"

        print(f"  {purpose:22s} {n:>5d} {cell('promptTokens'):>18s} {cell('outputTokens'):>14s} "
              f"{cell('loadMs'):>14s} {cell('totalMs'):>16s}")


def mine_wild(rows, n):
    rejections, tech_failures, zero_routed, boring = [], [], [], []
    for row in rows:
        parsed = row.get("parsedResolution") or {}
        result = row.get("result") or {}
        routing = row.get("routing") or {}
        effects = parsed.get("effects") or []
        accepted = parsed.get("accepted")
        tech = result.get("technicalFailure") or (row.get("provider") and result.get("success") is False and not accepted)

        if accepted is False and parsed.get("rejectedReason"):
            rejections.append(parsed["rejectedReason"].strip())
        codes = row.get("validationErrors") or []
        if codes:
            for code in codes:
                tech_failures.append(code)
        if result.get("technicalFailure") and not codes:
            tech_failures.append(row.get("result", {}).get("magic", {}).get("error", "unknown") or "unknown")
        if not (routing.get("selectedCapabilities") or []):
            zero_routed.append(row.get("spellText", "").strip())
        if effects and all((e.get("type") or "").lower() == "message" for e in effects):
            boring.append(row.get("spellText", "").strip())

    print(f"wild-magic casts analyzed: {len(rows)}")
    show_bucket("intentional rejections by reason", rejections, n)
    show_bucket("technical failures by validation code", tech_failures, n)
    show_bucket("casts that routed zero capabilities", zero_routed, n)
    show_bucket("boring casts (only message effects)", boring, n)

    def purpose_of(row):
        stats = (row.get("routing") or {}).get("providerStats")
        return ("wild-resolve", stats) if stats else None

    print_latency(provider_stats_rows(rows, purpose_of))


def mine_dialogue(rows, n):
    print(f"dialogue turns analyzed: {len(rows)}")
    errors = [r.get("error") for r in rows if r.get("technicalFailure") and r.get("error")]
    show_bucket("dialogue technical failures", errors, n)

    def purpose_of(row):
        stats = row.get("providerStats")
        return ("dialogue-speech", stats) if stats else None

    print_latency(provider_stats_rows(rows, purpose_of))


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", nargs="?", default="logs/wild_magic_audit.jsonl")
    parser.add_argument("--dialogue", help="also mine a dialogue audit log")
    parser.add_argument("--top", type=int, default=15)
    args = parser.parse_args(argv)

    mine_wild(load(args.path), args.top)
    if args.dialogue:
        print("\n" + "=" * 60)
        mine_dialogue(load(args.dialogue), args.top)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
