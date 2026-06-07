---
name: sync-contract-versioning
description: Use when changing API or event contracts shared between ERP, PDV, and sync to enforce semantic versioning and compatibility rules.
---

# Sync Contract Versioning

## Goal
Apply strict versioning policy for shared integration contracts.

## Workflow
1. Identify whether the change is `breaking` or `non-breaking`.
2. Update contract version using semver:
   - breaking -> major
   - non-breaking feature -> minor
   - fix/docs/example only -> patch
3. Update `CHANGELOG.md` with:
   - date
   - summary
   - migration note (if breaking)
4. Update examples for request/response and event payloads.
5. Ensure both ERP and PDV compatibility statement is explicit.

## Done Criteria
- Version updated
- Changelog updated
- Examples updated
- Compatibility note added

