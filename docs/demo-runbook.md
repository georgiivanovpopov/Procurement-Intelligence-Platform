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

## Production release on Render Standard

1. Apply `render.yaml` and confirm the service plan is **Standard**. Do not add a persistent disk: the verified SQLite snapshot remains read-only inside the image.
2. Run `/health/ready`, then `node scripts/load-smoke.mjs https://<render-host> 20`. Save the printed latency/status summary and do not claim results that were not measured.
3. Add `tenderlens.bg` and `www.tenderlens.bg` as custom domains in Render. At the registrar, copy Render's exact DNS targets; do not copy example values because targets can change.
4. Wait until Render reports both domains verified and TLS certificates issued. Confirm HTTP redirects to HTTPS, `https://tenderlens.bg/health/ready` returns 200, and the certificate covers the hostname.
5. Check a signal with more than 50 records: each response shows at most 50, navigation preserves `page`, `sort`, and `dir`, and Browser Back restores the state.
6. Confirm invalid pagination returns `400`/`invalid_pagination`, invalid sorting returns `invalid_sort`, and throttling returns `429`, `Retry-After`, and `rate_limit_exceeded`.
7. Roll back by redeploying the previous verified image digest; never rebuild an older source revision against a different snapshot.
