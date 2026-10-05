# Python vs C# A/B report

**Status: pending re-run.**

The previous numbers in this report were generated before the fixes from Rick's PR #321 review
(B1-B4 and related non-blocking items): the first-audio measurement had a ~1.2 s floor (B2), the
CPU/memory window didn't match the actual run (B3), and the summary overstated what the tables
showed (B4). The harness (`scripts/ab_compare.py`) has since been corrected and has new unit
tests covering each fix (`scripts/tests/test_ab_compare.py`).

The coordinator re-runs the harness live against both backends after this fix lands, and fills
in this report with the fresh measured numbers and only the claims the tables support. Until
then, treat any previously-circulated numbers from this report as stale.

See `scripts/ab_compare.py --help` for how to run it, and the harness's own module docstring for
exactly what each column measures.
