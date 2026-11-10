"""Optional Matplotlib export of one observed cohort; no fitting or efficacy claim."""
import argparse
import csv
from pathlib import Path
import statistics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--analysis", type=Path, required=True)
    parser.add_argument("--output-prefix", type=Path, required=True)
    args = parser.parse_args()
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    rows = [r for r in csv.DictReader((args.analysis / "blocks.csv").open(encoding="utf-8-sig")) if r["eligible"] == "True"]
    cohorts = {(r["split"], r["hardware_id"], r["workload_id"], r["regime"]) for r in rows}
    if len(cohorts) != 1:
        raise ValueError("This focused export needs exactly one split/hardware/workload/regime cohort")
    for suffix in (".png", ".svg"):
        if Path(str(args.output_prefix) + suffix).exists():
            raise FileExistsError("Use a new figure prefix")
    colors = ["#555555", "#009E73", "#D55E00", "#0072B2", "#CC79A7", "#E69F00"]
    labels = {"A": "A: no engine", "B": "B: dry run", "C": "C: fixed", "D": "D: V1", "E": "E: CARP", "F": "F: no BG cost"}
    fig, (latency, pareto) = plt.subplots(1, 2, figsize=(10.8, 5))
    for index, policy in enumerate("ABCDEF"):
        group = [r for r in rows if r["condition"] == policy]
        if not group:
            continue
        x = [float(r["foreground_p95_ms"]) for r in group]
        y = [float(r["background_throughput_units_per_s"]) for r in group]
        latency.scatter([index] * len(x), x, color=colors[index], s=26, alpha=.65)
        latency.scatter(index, statistics.mean(x), marker="_", color=colors[index], s=350, linewidths=2)
        pareto.scatter(x, y, color=colors[index], s=24, alpha=.45)
        pareto.scatter(statistics.mean(x), statistics.mean(y), color=colors[index], marker="D", s=65, label=labels[policy])
        pareto.annotate(policy, (statistics.mean(x), statistics.mean(y)), xytext=(5, 5), textcoords="offset points", fontsize=9)
    latency.set_xticks(range(6), list("ABCDEF"))
    latency.set_ylabel("Per-block foreground task p95 (ms)")
    latency.set_title("Raw session-block p95 and block mean")
    latency.set_ylim(bottom=0)
    pareto.set_xlabel("Mean block foreground p95 (ms), lower preferred")
    pareto.set_ylabel("Background fixture iterations/s, higher preferred")
    pareto.set_title("Descriptive foreground / background tradeoff")
    pareto.legend(fontsize=8, loc="best")
    for axis in (latency, pareto):
        axis.grid(alpha=.2)
        axis.set_axisbelow(True)
    fig.suptitle("Healthy synthetic pilot: 3 paired session blocks, one Windows PC", fontsize=12)
    fig.text(.02, .02, "Points are blocks, not independent requests. E/F had no accepted model candidate and performed no intervention.\n"
        "Not a real-app input-latency study; no CARP superiority or noninferiority claim.", fontsize=9)
    fig.tight_layout(rect=(0, .11, 1, .94))
    for suffix in (".png", ".svg"):
        fig.savefig(str(args.output_prefix) + suffix, dpi=200)
    svg = Path(str(args.output_prefix) + ".svg")
    svg.write_text("\n".join(line.rstrip() for line in svg.read_text(encoding="utf-8").splitlines()) + "\n", encoding="utf-8")
    plt.close(fig)


if __name__ == "__main__":
    main()
