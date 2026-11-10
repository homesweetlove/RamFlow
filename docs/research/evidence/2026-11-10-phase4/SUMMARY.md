# ResearchLab outcome analysis

Descriptive analysis; no efficacy verdict.

- Analysis unit: paired session block; requests are not independent replicates.
- Recorded outcomes: 36; eligible: 17; excluded or missing: 19.
- Foreground p95 tolerance: 1 ms (explicitly supplied).
- Bootstrap: 10000 paired-block draws; master seed 20261110; 95% percentile intervals.
- Supplied external p-values: 0. No p-values inferred from bootstrap intervals.

## Warnings

- No expected-outcome manifest: trials with no outcome file cannot be discovered.

## Per-hardware summaries

Equal-weight means of eligible block summaries; n = blocks.

| Split / hardware / workload / observed regime | Policy | Blocks | FG p95 ms | BG units/s |
| --- | --- | ---: | ---: | ---: |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | A:None | 3 | 7.0787 | 519.02 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | B:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 7.0511 | 531.35 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | C:None | 3 | 7.5046 | 221.52 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | D:None | 2 | 7.1799 | 364.14 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | E:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 7.0774 | 530.44 |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | F:WithoutBackgroundCost [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 7.1153 | 530.93 |

## Same-response comparisons

Point flags below require at least two complete pairs. They are descriptive and do not prove equivalence or joint confidence.

| Cohort | Candidate minus baseline | Paired blocks | FG difference ms [95% CI] | BG difference units/s [95% CI] | Within tolerance + positive BG (point only) |
| --- | --- | ---: | --- | --- | --- |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | B:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus A:None | 3 | -0.027603 [-0.1176099999999991, 0.07907499999999956] | 12.322 [3.303067184368615, 18.906509146694702] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | C:None minus A:None | 3 | 0.42586 [0.31552000000000024, 0.48187499999999783] | -297.5 [-305.76540808258005, -292.1156844572346] | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | D:None minus A:None | 2 | 0.12038 [0.01666499999999882, 0.22409999999999997] | -150.14 [-150.65656271077006, -149.62667299157636] | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | E:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus A:None | 3 | -0.0013333 [-0.07223499999999916, 0.03466000000000058] | 11.419 [5.573199540835958, 18.613664589149835] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | F:WithoutBackgroundCost [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus A:None | 3 | 0.036618 [0.007519999999999527, 0.06375000000000064] | 11.911 [-4.225754733653162, 21.273862684957635] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | C:None minus B:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 0.45346 [0.4011100000000001, 0.5261499999999986] | -309.83 [-311.0221936039293, -309.06847526694867] | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | D:None minus B:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 2 | 0.10298 [-0.06241000000000074, 0.2683750000000007] | -166.97 [-168.53318213827106, -165.41396157347123] | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | E:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus B:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 0.02627 [-0.04550000000000054, 0.07893500000000131] | -0.90298 [-8.835329581271253, 3.856265726448669] | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | F:WithoutBackgroundCost [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus B:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 0.064222 [-0.04049000000000014, 0.18135999999999974] | -0.41175 [-7.528821918021777, 3.9262139161006644] | False |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | D:None minus C:None | 2 | -0.36065 [-0.4635200000000008, -0.25777499999999787] | 143.23 [142.4890114656582, 143.97348509063826] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | E:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus C:None | 3 | -0.42719 [-0.44721499999999725, -0.3877549999999994] | 308.92 [302.186864022658, 313.24371239055813] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | F:WithoutBackgroundCost [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus C:None | 3 | -0.38924 [-0.4743549999999983, -0.2517699999999996] | 309.41 [301.5396533489269, 313.3895471421922] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | E:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus D:None | 2 | -0.086265 [-0.1894399999999994, 0.016910000000000203] | 164.48 [159.6978525569998, 169.2702272999199] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | F:WithoutBackgroundCost [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus D:None | 2 | -0.09733 [-0.21658000000000044, 0.021920000000000606] | 170.12 [169.3401754895719, 170.900535676534] | True |
| pilot / 251b947a03c93baa09a14ca24570e1e9e4c9c47cc99f54f955f8cc3224d45199 / synthetic-sha-pagewalk-v2:cpu:fg8:bg32:threads12:units0:gap5:observer-independent-v1:pre5 / cpu / pre-intervention-window-v1 | F:WithoutBackgroundCost [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] minus E:None [model=30dc959a9a0c01ba81ff54c0e31f0fbeb3aec151d73438bf34fb2a496993b943] | 3 | 0.037952 [-0.027140000000001052, 0.1359849999999998] | 0.49123 [-9.79895427448912, 11.202683119534186] | True |

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
- Schema 3 cohorts/eligibility use the frozen observed PRE-intervention window. Post-intervention regime remains an outcome and never excludes a trial. Legacy schema 2 keeps its historical post-regime exclusions and is never pooled with schema 3. Calibration is excluded.
- Model IDs and policy variants stay separate. No models are fitted or selected; fit/validation/pilot summaries remain exploratory.
- Pareto points use exactly the same complete blocks for both policies in each panel; frontiers are descriptive only, without a success claim.
- Same-response flags require >=2 complete pairs, explicit foreground p95 tolerance and positive background throughput difference; marginal CI checks do not establish joint coverage or practical equivalence.
- Background throughput is observed useful units / measurement seconds; missing completion times are never inferred from throughput.
- Overhead is summarized within outcome/role/phase first, then equally across blocks. Sample counts are diagnostic; CPU normalization is producer-defined, not inferred.
- Missing files can only be identified from an explicit expected-outcome manifest; without it, undiscovered trials cannot be enumerated.
