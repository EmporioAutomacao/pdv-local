# PDV Local

Este diretorio e dedicado ao projeto do PDV local.

## Versao atual

**1.3.3** (2026-09-01). Manual do operador: `docs/pdv-app-manual-operador.md`.
Referencia tecnica de atalhos/comportamentos: `docs/pdv-app-atalhos-e-comportamentos.md`.
Instalacao em producao: `docs/manual-instalacao-producao.md`.

## Objetivo
- Aplicacao local comercializavel em larga escala.
- Operacao offline-first.
- Sincronizacao segura com o ERP em nuvem.

## Escopo deste projeto
- UI local Windows com WPF em .NET 8.
- Servicos locais do PDV.
- Integracoes locais (impressora, serial, perifericos).
- Cliente de sincronizacao com API do ERP.
- App de bandeja do Windows para diagnostico do Sync Agent.

## Integracao
- Este projeto **nao** define contrato de API por conta propria.
- Todo contrato deve vir de `../sync`.
- O SyncAgent ja possui dispatcher configuravel para enviar outbox ao endpoint
  contratado `POST /v1/sync/events:batch`.

## Persistencia local
- O banco local padrao do PDV/sync-agent sera PostgreSQL com pgvector, alinhado ao ERP.
- Ambiente local: `pgvector/pgvector:0.8.0-pg17`.

## Instalacao Windows
- Docker Compose e usado apenas para desenvolvimento.
- Cliente final deve usar instalacao Windows com PostgreSQL local e `SyncAgent` como servico.
- Guia operacional: `docs/windows-installation.md`.

## Status da Sprint 1
- Plano tecnico do PDV App: `docs/pdv-technical-plan.md`.
- Estrutura de desenvolvimento e VM de homologacao: `docs/development-structure-and-vm-runbook.md`.
- Status tecnico atual e roadmap: `docs/sync-agent-sprint-1-status.md`.
- Manual tecnico consolidado: `docs/sync-agent-sprint-1-manual.md`.
- Runbook de incidentes: `docs/sync-agent-runbook-incidentes.md`.
- Readiness do piloto tecnico: `docs/sync-agent-piloto-readiness.md`.
- Preparacao do coletor Arpa: `docs/arpa-collector-piloto.md`.
- Politica de seguranca Arpa read-only: `docs/arpa-readonly-security-policy.md`.
- Decisao de watermark de produtos Arpa: `docs/arpa-produtos-watermark-decision.md`.
- Piloto Arpa Anapolis: `docs/arpa-piloto-anapolis.md`.
- Smoke test operacional: `infra/windows/test-sync-agent-smoke.ps1`.


