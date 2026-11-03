Every parcel line carries exactly one of: CONFIRMED (filled circle, `confirmed`), FLAGGED (triangle, `flagged`), pending (half circle, `pending`) or NOT_ASSESSABLE (dashed circle, `not-assessable`). The pill always shows icon and word; colour is the third cue, never the only one.

FLAGGED means "a measured fact disagrees with the declaration and a person should look". It is never labelled Rejected, Failed or High risk. NOT_ASSESSABLE and "Below resolution" share the grey dashed circle because the test could not run. Never show them as "No crop".

Status fills use the `-bg` tokens, and the text colour is the matching status token. Do not reuse the A to E band colours from the Zr3i deck for these states; those bands belong to the Risk module only (RiskBand) and are never used as verdict colours.
