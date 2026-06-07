---
name: sync-release-readiness
description: Use before release to verify technical readiness for ERP-PDV-sync deployments.
---

# Sync Release Readiness

## Goal
Standard pre-release gate for sync-related changes.

## Checklist
1. Contract version and changelog updated
2. Backward compatibility validated
3. Metrics and alerts configured
4. Retry and dead-letter behavior validated
5. Security checks passed (auth, mTLS, secrets)
6. Runbook updated
7. Rollback rehearsed

## Exit Rule
Release is blocked if any mandatory item fails.

