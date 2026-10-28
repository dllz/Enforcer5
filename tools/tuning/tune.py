#!/usr/bin/env python3
"""Precision and recall of Detection's similarity thresholds (design section 11, build step 13).

Reads the CSV from export.sql: one row per hit or near miss of a comparing sign, with how alike it was and what the
admins decided about the member afterwards. For each sign (and each group size), and each threshold from 0.40 to 1.00:

  precision  of the members at or above the threshold that an admin decided about, the share removed or muted
  recall     of all the members removed or muted, the share at or above the threshold

and proposes the lowest threshold whose precision is at least --precision with at least --min decided members. Only
members with a decision count; the rest say nothing either way. A proposed threshold goes to SimilarityTuning (or the
Similarity* setting of the same name) and runs in shadow first.

    python3 tools/tuning/tune.py /workspace/scratch/similarity.csv [--precision 0.9] [--min 30] [--plot out.png]
"""
import argparse
import csv
import sys
from collections import defaultdict

SETTING = {"same_as_newcomers": "SimilarityText", "name_impersonation": "SimilarityNameNear (and the name distances)"}


def truthy(value):
    return str(value).strip().lower() in ("t", "true", "1", "yes")


def curve(rows, thresholds):
    bad_total = sum(1 for r in rows if r["bad"])
    points = []
    for t in thresholds:
        above = [r for r in rows if r["similarity"] >= t]
        decided = [r for r in above if r["bad"] or r["fine"]]
        bad = sum(1 for r in decided if r["bad"])
        precision = bad / len(decided) if decided else None
        recall = bad / bad_total if bad_total else None
        points.append((t, len(above), len(decided), precision, recall))
    return points


def propose(points, target, minimum):
    for t, _, decided, precision, _ in points:
        if precision is not None and decided >= minimum and precision >= target:
            return t
    return None


def fmt(v):
    return "  -  " if v is None else f"{v:5.0%}"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("csv")
    parser.add_argument("--precision", type=float, default=0.9, help="precision a proposal must reach (default 0.9)")
    parser.add_argument("--min", type=int, default=30, help="decided members a proposal needs (default 30)")
    parser.add_argument("--plot", help="write a precision/recall chart to this PNG")
    args = parser.parse_args()

    rows = []
    with open(args.csv, newline="") as f:
        for r in csv.DictReader(f):
            rows.append({"sign": r["sign"], "kind": r["kind"], "similarity": float(r["similarity"]), "bad": truthy(r["bad"]),
                         "fine": truthy(r["fine"]), "size": r.get("group_size", "all")})
    if not rows:
        print("No rows: nothing compared yet.")
        return 0

    thresholds = [round(0.40 + 0.05 * i, 2) for i in range(13)]
    by_sign = defaultdict(list)
    for r in rows:
        by_sign[r["sign"]].append(r)

    charts = {}
    for sign, sign_rows in sorted(by_sign.items()):
        print(f"\n== {sign}  ({len(sign_rows)} rows; setting {SETTING.get(sign, '?')})")
        for size in ["all", "small", "medium", "large"]:
            part = sign_rows if size == "all" else [r for r in sign_rows if r["size"] == size]
            if not part:
                continue
            points = curve(part, thresholds)
            if size == "all":
                charts[sign] = points
                print("threshold  rows  decided  precision  recall")
                for t, n, d, p, rc in points:
                    print(f"  {t:4.2f}   {n:6d}  {d:6d}    {fmt(p)}    {fmt(rc)}")
            best = propose(points, args.precision, args.min)
            print(f"{size:>7}: proposed threshold "
                  f"{best if best is not None else 'none (too few decisions, or never precise enough)'}")

    if args.plot and charts:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, ax = plt.subplots(figsize=(8, 5))
        for sign, points in charts.items():
            xs = [p[0] for p in points]
            ax.plot(xs, [p[3] if p[3] is not None else float("nan") for p in points], marker="o", label=f"{sign} precision")
            ax.plot(xs, [p[4] if p[4] is not None else float("nan") for p in points], linestyle="--", label=f"{sign} recall")
        ax.set_xlabel("threshold")
        ax.set_ylim(0, 1.05)
        ax.legend()
        ax.grid(alpha=0.3)
        fig.tight_layout()
        fig.savefig(args.plot, dpi=120)
        print(f"\nChart: {args.plot}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
