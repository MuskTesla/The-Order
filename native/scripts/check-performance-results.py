"""Check checkpoint-bound Phase 7 fixtures. Absolute latency budgets are machine-local."""
import argparse
import json
import statistics
from pathlib import Path


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def median(values):
    return statistics.median(values)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bench", type=Path, required=True)
    parser.add_argument("--checkpoints", type=Path, required=True)
    parser.add_argument("--media", type=Path, required=True)
    parser.add_argument("--ui", type=Path, required=True)
    parser.add_argument("--soak", type=Path, required=True)
    parser.add_argument("--ui-soak", type=Path)
    parser.add_argument("--ui-resources", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--thresholds", type=Path)
    args = parser.parse_args()
    checks, observations, baselines = [], [], {}

    def check(name, actual, limit):
        checks.append(dict(name=name, actual=actual, limit=limit, passed=actual <= limit))

    def latency(name, p95, floor):
        # Derive a reproducible machine-local reference with margin, never a universal SLA.
        baselines[name] = dict(p95=p95, limitMs=max(floor, p95 * 2))
        if args.thresholds:
            check(name, p95, load(args.thresholds)["latency"][name]["limitMs"])

    for dataset in load(args.bench / "library-results.json"):
        n = dataset["identity"]["count"]
        for operation in dataset["operations"]:
            latency(f"library-{n}/{operation['name']}", operation["p95"], 100 if operation["name"].startswith(("sort-", "exact-", "alias", "no-match")) else 1000)
    for dataset in load(args.bench / "history-results.json"):
        n = dataset["identity"]["history"]
        for operation in dataset["operations"]:
            latency(f"history-{n}/{operation['name']}", operation["p95"], 2000)
    for dataset in load(args.checkpoints / "checkpoint-results.json"):
        latency(f"checkpoint-{dataset['history']}", dataset["measurement"]["p95"], 2000)
        check(f"checkpoint-{dataset['history']}/accounting", int(not(dataset["countUnchanged"] and dataset["exactlyOnce"])), 0)

    media_summary = []
    for dataset in load(args.media / "media-results.json"):
        times = [p["elapsedMs"] for p in dataset["passes"] if not p["warmup"]]
        n = dataset["identity"]["count"]
        media_summary.append(dict(count=n, medianMs=median(times), p95Ms=max(times), minMs=min(times), maxMs=max(times), measuredSamples=len(times)))
        for run in dataset["passes"]:
            for sample in run["resources"]:
                m = sample["metrics"]
                for field, limit in [("CachedBytes", 64 * 1024**2), ("CachedItems", 512), ("MaximumActiveDecodes", 2), ("PendingOperations", 256), ("PendingWaiters", 256)]:
                    check(f"media-{n}/{run['pass']}/{sample['index']}/{field}", m[field], limit)
            check(f"media-{n}/{run['pass']}/diskBytes", run["diskBytes"], 128 * 1024**2)
            for field in ["Errors", "Outstanding", "ActiveDecodes", "PendingOperations", "PendingWaiters"]:
                check(f"media-{n}/{run['pass']}/final-{field}", run["metrics"][field], 0)

    ui_summary = []
    ui_runs = load(args.ui / "ui-runs.json")
    for n in [1000, 5000, 10000]:
        runs = [r for r in ui_runs if r["count"] == n]
        warm = [r for r in runs if not r["warmup"]]
        item = dict(count=n, coldish=runs[0]["startup"], warm={})
        for name in ["profileOpenMs", "viewModelReadyMs", "firstUsableMs", "firstCoverMs"]:
            values = [r["startup"][name] for r in warm]
            item["warm"][name] = dict(median=median(values), p95=max(values), min=min(values), max=max(values), samples=len(values))
            latency(f"ui-{n}/{name}", max(values), 5000)
        workload = load(Path(runs[-1]["directory"]) / "ui-result.json")
        samples = workload["samples"]
        check(f"ui-{n}/completed", int(workload["status"] != "UI_WORKLOAD_COMPLETED"), 0)
        check(f"ui-{n}/realized", max(s["viewport"]["realizedContainerCount"] for s in samples), 256)
        check(f"ui-{n}/imageSurfaces", max(s["imageSurfaces"] for s in samples), 256)
        window = max(1, len(samples) // 4)
        # Exclude initial XAML/font materialization; compare two mature workload windows.
        first, last = samples[window:window*2], samples[-window:]
        check(f"ui-{n}/mature-handle-growth", median([s["handles"] for s in last]) - median([s["handles"] for s in first]), 64)
        check(f"ui-{n}/mature-private-growth", median([s["privateBytes"] for s in last]) - median([s["privateBytes"] for s in first]), 256 * 1024**2)
        item["workload"] = dict(rounds=workload["rounds"], peakWorkingSet=max(s["workingSet"] for s in samples), peakPrivate=max(s["privateBytes"] for s in samples), realizedMaximum=max(s["viewport"]["realizedContainerCount"] for s in samples), surfacesMaximum=max(s["imageSurfaces"] for s in samples), firstMatureHandles=median([s["handles"] for s in first]), lastMatureHandles=median([s["handles"] for s in last]), uiTickGapMedian=median(workload["uiTickGapsMs"]), uiTickGapMaximum=max(workload["uiTickGapsMs"]))
        ui_summary.append(item)

    soak = load(args.soak / "soak-result.json")
    check("soak/duration-minimum", 7200, soak["durationSeconds"])
    check("soak/failures", soak["failures"], 0)
    samples = soak["samples"]
    batches = []
    for batch in sorted({s["batch"] for s in samples}):
        part = [s for s in samples if s["batch"] == batch]
        row = dict(batch=batch, samples=len(part), startSeconds=part[0]["seconds"], endSeconds=part[-1]["seconds"], completedOperations=part[-1]["operations"] - (batches[-1]["lastOperations"] if batches else 0), lastOperations=part[-1]["operations"])
        for field in ["workingSet", "privateBytes", "managedHeap", "handles", "threads", "user", "gdi", "pendingThreadPool"]:
            values = [s["resources"][field] for s in part]
            row[field] = dict(median=median(values), min=min(values), max=max(values))
        batches.append(row)
        for sample in part:
            m = sample["image"]
            for field, limit in [("CachedBytes", 64 * 1024**2), ("CachedItems", 512), ("Outstanding", 0), ("ActiveDecodes", 0), ("PendingOperations", 0), ("PendingWaiters", 0), ("Errors", 0)]:
                check(f"soak/{sample['round']}/{field}", m[field], limit)
    mature = [s for s in samples if s["seconds"] >= 900]
    first, last = mature[:len(mature)//4], mature[-len(mature)//4:]
    for field, allowance in [("handles", 32), ("user", 4), ("gdi", 4), ("privateBytes", 256*1024**2)]:
        delta = median([s["resources"][field] for s in last]) - median([s["resources"][field] for s in first])
        check("soak/mature-growth/" + field, delta, allowance)
    check("soak/last-pending-threadpool", last[-1]["resources"]["pendingThreadPool"], 0)
    if any(r["workload"]["uiTickGapMaximum"] > 1000 for r in ui_summary):
        observations.append("Actual diagnostics UI timer saw a gap over 1 second; retain raw samples and correlate GC/loading.")

    sustained_ui = None
    if args.ui_soak:
        run = load(args.ui_soak / "ui-result.json")
        check("ui-soak/duration-minimum", 7200, run["durationSeconds"])
        check("ui-soak/completed", int(run["status"] != "UI_WORKLOAD_COMPLETED"), 0)
        ui_samples = run["samples"]
        mature_ui = [s for s in ui_samples if s["elapsedMs"] >= 900000]
        first, last = mature_ui[:len(mature_ui)//4], mature_ui[-len(mature_ui)//4:]
        for field, allowance in [("handles", 64), ("privateBytes", 256*1024**2)]:
            check("ui-soak/mature-growth/" + field, median([s[field] for s in last]) - median([s[field] for s in first]), allowance)
        for s in ui_samples:
            for field, limit in [("CachedBytes",64*1024**2),("CachedItems",512),("MaximumActiveDecodes",2),("PendingOperations",256),("PendingWaiters",256),("Errors",0)]:
                check(f"ui-soak/{s['round']}/{field}", s["images"][field], limit)
            check(f"ui-soak/{s['round']}/surfaces", s["imageSurfaces"], 256)
            check(f"ui-soak/{s['round']}/realized", s["viewport"]["realizedContainerCount"], 256)
        gaps = sorted(run["uiTickGapsMs"])
        sustained_ui = dict(durationSeconds=run["durationSeconds"], rounds=run["rounds"], peakWorkingSet=max(s["workingSet"] for s in ui_samples), peakPrivate=max(s["privateBytes"] for s in ui_samples), maturePrivateMedian=median(s["privateBytes"] for s in mature_ui), firstMatureHandles=median(s["handles"] for s in first), lastMatureHandles=median(s["handles"] for s in last), peakSurfaces=max(s["imageSurfaces"] for s in ui_samples), uiTickGapMedian=median(gaps), uiTickGapP95=gaps[max(0,int(len(gaps)*.95)-1)], uiTickGapMaximum=max(gaps))
        if max(gaps) > 1000:
            observations.append("Sustained XAML diagnostics timer saw a gap over 1 second; retain raw timing and correlate workload/GC.")
    os_ui = None
    if args.ui_resources:
        run = load(args.ui_resources / "result.json")
        check("ui-os/process-exited", int(run["status"] != "PROCESS_EXITED"), 0)
        os_samples = run["samples"]
        # The watcher starts after initial XAML materialization.
        first, last = os_samples[:len(os_samples)//4], os_samples[-len(os_samples)//4:]
        for field, allowance in [("user",4),("gdi",4)]:
            check("ui-os/mature-growth/" + field, median(s[field] for s in last) - median(s[field] for s in first), allowance)
        os_ui = {field:dict(min=min(s[field] for s in os_samples),median=median(s[field] for s in os_samples),max=max(s[field] for s in os_samples)) for field in ["handles","threads","user","gdi","privateBytes","workingSet"]}
    report = dict(status="PASS" if all(c["passed"] for c in checks) else "FAILED", checks=checks, failures=[c for c in checks if not c["passed"]], ui=ui_summary, media=media_summary, soak=dict(durationSeconds=soak["durationSeconds"], rounds=soak["rounds"], operations=soak["operations"], batches=batches), sustainedUi=sustained_ui, uiOsResources=os_ui, observations=observations)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "performance-summary.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if not args.thresholds:
        (args.output / "thresholds.json").write_text(json.dumps(dict(kind="machine-local measured p95 x2 with floors; hard resource budgets are encoded in checker", environment=load(args.bench / "environment.json"), latency=baselines), ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(status=report["status"], checks=len(checks), failures=report["failures"], observations=observations), ensure_ascii=False))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
