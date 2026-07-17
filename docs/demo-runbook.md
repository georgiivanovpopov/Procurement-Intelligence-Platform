# TenderLens demo runbook

1. Run `dotnet test TenderLens.slnx` and generate/package the snapshot using the commands in `README.md`.
2. Record raw and compressed sizes and SHA-256 from `scripts/package-snapshot.ps1`. Confirm the archive fits the free Render image before changing the bundled release.
3. Query `/api/v1/meta`: `observationStart` must begin in 2020, `observationEnd` must match the newest included record, and all included source families must be listed.
4. Confirm `/health/ready` returns `200` after the container or Render service wakes.
5. Open a curated supplier that occurs in multiple years. Confirm the contract count, coverage label and currency state are truthful.
6. Open Buyer concentration or Repeated relationship and then a historical evidence row. Confirm the record exposes its manifest resource ID and official public link.
7. Open Amendment intensity for a supplier with normalized annexes and verify its evidence count agrees with the record detail.
8. Return with Browser Back and confirm context is preserved.
9. Check a record with a missing amount. It must not contribute zero to a monetary total, and mixed/unknown currencies must show insufficient data.
10. Before presenting, keep the previous verified snapshot available; a failed required resource must never replace it.

Verified production snapshot: 12,994 suppliers, 134,490 records, 2020-01-01–2026-06-30. Suggested demo EIKs: `103267194`, `203283623`, `131268894`, `102227154`, `831641528`.

Known coverage limits: the pinned historical inventory contains contract and annex CSV resources for 2020–2025. OCDS remains the current structured source for 2026 onward. Historical CSV does not reliably expose bid counts or every CPV/annex value, so those calculations remain explicitly unavailable where inputs are insufficient.
