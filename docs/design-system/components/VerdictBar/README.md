Show the verdict mix as one stacked bar with counts in a legend underneath. Segments are ordered Confirmed, Flagged, Pending, Not assessable and always sum to the parcel total shown above the bar.

Not assessable is drawn with a diagonal hatch so it stays distinct when printed in grey. Give the bar an `aria-label` that lists every count.

Do not show a single percentage such as "58% good". Readable share is reported by size class (see IntervalChart), never pooled.
