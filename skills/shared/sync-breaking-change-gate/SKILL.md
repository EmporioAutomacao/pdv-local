---
name: sync-breaking-change-gate
description: Use before approving breaking changes in shared contracts to enforce migration gates and no-go checks.
---

# Sync Breaking Change Gate

## Goal
Prevent unsafe breaking changes in integration contracts.

## Required Gates
1. Breaking change labeled explicitly.
2. Migration guide documented.
3. Dual-read or fallback strategy defined.
4. Rollout plan by wave documented.
5. Rollback plan documented and tested.
6. Approval from ERP owner and PDV owner.

## No-Go Conditions
- No migration guide
- No rollback plan
- No test evidence
- No owner approval

