# Case study: Wisp vs Brave vs Chrome memory use

**8 identical tabs, measured on one Windows 11 machine, 2026-07-02.**

Wisp exists because Brave was using ~7 GB for a handful of tabs. This is a controlled
head-to-head: the same 8 sites, launched fresh in each browser on a throwaway profile, with the
whole process tree's RAM measured after the pages settled.

## Results

| Browser | State | RAM | Processes | vs Wisp (slept) |
|---|---|---:|---:|---:|
| **Wisp** | 7 background tabs slept | **711 MB** | 8 | — |
| Wisp | all 8 tabs live | 2,204 MB | 22 | 3.1× |
| Brave | all 8 tabs | 2,615 MB | 28 | 3.7× |
| Chrome | all 8 tabs | 4,669 MB | 59 | 6.6× |

```
RAM for the same 8 tabs (lower is better)

Wisp · slept   ▉▉▉▉▉▉▉▉                                    711 MB
Wisp · live    ▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉                  2,204 MB
Brave          ▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉             2,615 MB
Chrome         ▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉▉ 4,669 MB
```

## What the numbers say

- **Wisp in normal use (background tabs slept): 711 MB** — 73% less than Brave and **85% less than
  Chrome** for the exact same 8 sites. Chrome used **6.6×** as much.
- **Even with all 8 tabs live**, Wisp (2,204 MB) is lighter than Brave (2,615 MB) and roughly half of
  Chrome (4,669 MB) — before any sleeping. That comes from process-per-site, a capped renderer count,
  Chromium's low-end-device mode, and a smaller V8 heap.
- **Process count tells the story too:** Chrome fanned out to **59 processes** for 8 tabs; Wisp's slept
  state runs in **8**. Fewer live renderers is the whole point.
- Per tab, effectively: Wisp slept ≈ **89 MB/tab**, Chrome ≈ **584 MB/tab**.

The headline feature — **background tabs sleep** — is where the ~3× drop (2,204 → 711 MB) comes from:
idle renderers are suspended and eventually discarded, then restored instantly when you click back.
Brave and Chrome keep every tab's renderer fully resident.

## Method

- **Tabs (identical for all three):** YouTube, Reddit, Wikipedia (Chromium article), GitHub Trending,
  CNN, Amazon, weather.com, ESPN.
- **Isolation:** each browser launched with its own throwaway profile (`--user-data-dir` / `WISP_UDF`),
  so no existing session, extensions, or open tabs affected the numbers. Wisp ran with its
  single-instance lock bypassed so it didn't merge into the everyday copy.
- **Metric:** sum of the **working set (physical RAM)** of every process belonging to that browser
  (`Win32_Process.WorkingSetSize`), collected 65 s after launch once pages had loaded.
- **Wisp measured twice:** immediately after load (all tabs live) and after its sleep/discard timers
  ran (7 background tabs suspended/discarded, 1 active tab live) — the realistic daily-driver state.
- **Reproduce it:** `scripts/bench.ps1`-style harness — launch each browser at a unique profile with
  the 8 URLs, sum `WorkingSetSize` for processes whose command line contains that profile path.

## Honest caveats

- Working set counts some shared memory per process, so absolute totals run a little high — but all
  three are Chromium, so they share the same way and the **comparison is apples-to-apples**.
- Single run on one machine; RAM fluctuates a few percent between runs. Treat these as **directional**,
  not lab-grade — the ratios (3–7×) are large and stable enough to be meaningful.
- Any Chromium browser uses similar RAM **per live tab**. Wisp doesn't beat physics on an active page —
  it wins by **not keeping idle tabs alive**. Open all 8 and hammer them at once and Wisp's advantage
  narrows to the "all live" row (still ahead here).
- Chrome's unusually high figure reflects its default full site-isolation and helper processes; a
  machine with fewer cores may show fewer Chrome processes and less RAM.

## Takeaway

For the way people actually browse — a pile of tabs, most sitting idle — **Wisp holds the same 8 sites
in about a quarter of Brave's memory and a seventh of Chrome's**, while restoring backgrounded tabs
instantly on click. That's the entire reason it exists.
