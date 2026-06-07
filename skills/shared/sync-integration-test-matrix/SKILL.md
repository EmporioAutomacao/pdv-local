---
name: sync-integration-test-matrix
description: Use when validating ERP-PDV-sync communication to generate and execute an integration test matrix.
---

# Sync Integration Test Matrix

## Goal
Validate communication contracts with deterministic coverage.

## Matrix Dimensions
1. Entity type: customer, product, stock, sale, finance
2. Operation: create, update, status change
3. Network state: online, intermittent, offline-recovery
4. Data quality: valid, partial, invalid payload
5. Idempotency: first send, duplicate send

## Output
- Test matrix table
- Expected outcomes
- Evidence checklist

## Done Criteria
- All critical paths executed
- Idempotency verified
- Offline recovery verified
- Failures mapped with root cause

