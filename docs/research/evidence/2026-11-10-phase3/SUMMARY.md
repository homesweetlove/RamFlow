# ResearchLab outcome analysis

Descriptive analysis; no efficacy verdict.

- Analysis unit: paired session block; requests are not independent replicates.
- Recorded outcomes: 36; eligible: 12; excluded or missing: 24.
- Foreground p95 tolerance: 1 ms (explicitly supplied).
- Bootstrap: 10000 paired-block draws; master seed 20261110; 95% percentile intervals.
- Supplied external p-values: 0. No p-values inferred from bootstrap intervals.

## Warnings

- No expected-outcome manifest: trials with no outcome file cannot be discovered.

## Per-hardware summaries

Equal-weight means of eligible block summaries; n = blocks.

| Split / hardware / workload / observed regime | Policy | Blocks | FG p95 ms | BG units/s |
| --- | --- | ---: | ---: | ---: |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | A:None | 3 | 7.0611 | 523.89 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | B:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 3 | 7.07 | 524.85 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | E:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 3 | 7.0751 | 527.73 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | F:WithoutBackgroundCost [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 3 | 7.0437 | 528.47 |

## Same-response comparisons

Point flags below require at least two complete pairs. They are descriptive and do not prove equivalence or joint confidence.

| Cohort | Candidate minus baseline | Paired blocks | FG difference ms [95% CI] | BG difference units/s [95% CI] | Within tolerance + positive BG (point only) |
| --- | --- | ---: | --- | --- | --- |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | B:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus A:None | 3 | 0.0088917 [-0.023155000000000037, 0.037560000000000926] | 0.96439 [-3.0462243471705506, 8.55309222675021] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | D:None minus A:None | 0 | NA NA | NA NA | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | E:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus A:None | 3 | 0.013997 [-0.04440000000000133, 0.11669000000000018] | 3.8386 [-4.448255235565853, 19.409118990646334] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | F:WithoutBackgroundCost [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus A:None | 3 | -0.017385 [-0.054100000000000925, 0.025454999999999117] | 4.5849 [-0.09900611779733026, 13.686797166326073] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | D:None minus B:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 0 | NA NA | NA NA | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | E:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus B:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 3 | 0.005105 [-0.06786000000000048, 0.10442000000000018] | 2.8742 [-1.834545864076972, 10.856026763896125] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | F:WithoutBackgroundCost [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus B:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 3 | -0.026277 [-0.06106999999999996, 0.013184999999999114] | 3.6205 [2.780478653227874, 5.133704939575864] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | E:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus D:None | 0 | NA NA | NA NA | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | F:WithoutBackgroundCost [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus D:None | 0 | NA NA | NA NA | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v1:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1 / cpu | F:WithoutBackgroundCost [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] minus E:None [model=6d23a96a138896bba251481a7150ab8647f21add8d5d1aa98435a21e6c546cb0] | 3 | -0.031382 [-0.09123500000000107, 0.006790000000000518] | 0.74623 [-5.722321824320261, 4.615024517304846] | True |

## Artifacts

Each CSV table has a JSON counterpart. Full nested raw outcomes, hashes and exclusion reasons are in ledger.json; malformed raw text is preserved there.

- blocks: per-outcome latency, throughput, completion and reaccess summaries.
- per_hardware: separate split/hardware/workload/observed-regime/policy/model means.
- comparisons: all policy-pair raw differences, ratios, paired dz and block-bootstrap intervals.
- pairing: union of known blocks for each policy pair, including excluded and unpaired blocks.
- same_response: common complete-block foreground/background contrasts and explicit tolerance checks.
- ablations: F variants compared with E within the same cohort and block.
- overhead_blocks / overhead: role and phase separated, raw samples retained in ledger.
- failures: every efficacy exclusion, including calibration and known missing files (not necessarily runtime failure).
- p_values: externally supplied predeclared complete families only, Holm step-down adjustment.
- pareto: common-pair means and raw block coordinates; pareto.svg is descriptive.
- effect_*.svg: raw-unit difference intervals with units and block n; unavailable intervals labelled NA.
- overhead_*.svg / failures.svg: zero-baseline descriptive block summaries and outcome counts.
- manifest.json: provenance and SHA-256 hashes of every generated artifact except itself.

## Methods and limitations

- Descriptive analysis, not an efficacy verdict or a confirmatory test. No p-values are generated.
- The independent analysis unit is the paired session block, never requests or overhead samples.
- Block independence, randomized order, carryover control and workload comparability require external verification.
- Means give equal weight to blocks; per-block quantiles use linear interpolation (type 7). p99 requires >=1000 values in that block and channel.
- 95% percentile bootstrap intervals resample complete baseline/candidate block pairs with replacement; <2 pairs yields no interval.
- Intervals are marginal, unadjusted, exploratory and unreliable with few blocks; they are not simultaneous confidence or equivalence tests.
- Difference = mean(candidate - baseline); ratio = mean(candidate) / mean(baseline), not mean of block ratios.
- Paired standardized dz = mean(block differences) / sample SD(block differences), without small-sample correction. Zero SD or <2 blocks is undefined; dz is not comparable across dependence structures.
- Undefined ratio/dz bootstrap draws are counted; their entire interval is withheld rather than silently dropping draws.
- Complete-case contrasts may be biased by missingness. All excluded, failed, aborted, invalid, duplicate and known missing outcomes remain in the ledger.
- Regime is actually observed, not intended. Unattained regimes and calibration outcomes are excluded from efficacy; splits, hardware, workloads and regimes are never pooled.
- Model IDs and policy variants stay separate. No models are fitted or selected; fit/validation/pilot summaries remain exploratory.
- Pareto points use exactly the same complete blocks for both policies in each panel; frontiers are descriptive only, without a success claim.
- Same-response flags require >=2 complete pairs, explicit foreground p95 tolerance and positive background throughput difference; marginal CI checks do not establish joint coverage or practical equivalence.
- Background throughput is observed useful units / measurement seconds; missing completion times are never inferred from throughput.
- Overhead is summarized within outcome/role/phase first, then equally across blocks. Sample counts are diagnostic; CPU normalization is producer-defined, not inferred.
- Missing files can only be identified from an explicit expected-outcome manifest; without it, undiscovered trials cannot be enumerated.
