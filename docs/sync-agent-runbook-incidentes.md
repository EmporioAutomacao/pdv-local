# Runbook de Incidentes - Sync Agent

## Objetivo

Orientar suporte tecnico na triagem de falhas do Sync Agent entre PDV/local,
Arpa e ERP.

## Fontes de diagnostico

Local no cliente:

```text
http://127.0.0.1:47891/
http://127.0.0.1:47891/status
http://127.0.0.1:47891/help
```

ERP:

```text
GET /v1/sync/agents/{instanceId}/status
Admin > Sync installations
Admin > Sync received events
Admin > Sync reconciliation runs
```

Banco local:

```sql
SELECT * FROM sync_agent.reconciliation_runs ORDER BY started_at_utc DESC LIMIT 5;
SELECT status, count(*) FROM sync_agent.outbox_events GROUP BY status;
SELECT entity_type, reason, count(*) FROM sync_agent.dead_letter_events GROUP BY entity_type, reason;
```

## Severidade

| Severidade | Condicao | Acao |
|---|---|---|
| SEV1 | perda confirmada de eventos, corrupcao de dados ou duplicidade de efeito de negocio | parar rollout, preservar banco local, acionar engenharia |
| SEV2 | agente offline por mais de 15 min, fila crescendo, reconciliacao divergente | atuar no mesmo dia, coletar evidencias |
| SEV3 | alerta pontual sem impacto operacional | acompanhar e registrar |

## Incidente: agente offline

Sinais:

- ERP retorna `connectivity=offline`;
- `/status` local nao responde;
- Windows Service parado.

Passos:

1. Verificar servico:

```powershell
Get-Service "PDV Local Sync Agent"
```

2. Iniciar se estiver parado:

```powershell
Start-Service "PDV Local Sync Agent"
```

3. Validar dashboard local:

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status
```

4. Se falhar, coletar logs do Windows Event Viewer e preservar `pdv_sync`.

## Incidente: fila pendente crescendo

Sinais:

- `pending_outbox_events` aumenta continuamente;
- `oldest_pending_age_seconds` cresce;
- heartbeat pode estar `degraded`.

Passos:

1. Validar internet e DNS do endpoint ERP.
2. Validar `ErpApiBaseUrl`.
3. Validar token/certificado.
4. Executar sincronizacao manual:

```powershell
Invoke-RestMethod -Method Post http://127.0.0.1:47891/sync-now
```

5. Conferir `last_error` em `/status`.

## Incidente: dead-letter

Sinais:

- `dead_letter_events > 0`;
- dashboard/tray indicam DLQ.

Passos:

1. Consultar motivos:

```sql
SELECT entity_type, entity_key, reason, failed_at_utc
FROM sync_agent.dead_letter_events
ORDER BY failed_at_utc DESC
LIMIT 50;
```

2. Se erro for payload invalido, corrigir normalizador/contrato antes de reprocessar.
3. Se erro for dependencia ausente no ERP, criar/corrigir entidade base e reprocessar manualmente com engenharia.
4. Nunca apagar DLQ para "limpar painel" sem decisao tecnica.

## Incidente: reconciliacao divergente

Sinais:

- `remote_matched=false` em `sync_agent.reconciliation_runs.summary`;
- ERP `SyncReconciliationRun.matched=false`;
- endpoint central indica `connectivity=degraded`.

Passos:

1. Comparar `mismatches` no ERP.
2. Identificar entidade e campo divergente: `captured`, `accepted`, `rejected`, `dead_letter` ou `aggregate_hash`.
3. Se apenas `aggregate_hash` divergir com contadores iguais, comparar eventos por `payload_hash`.
4. Se `accepted` local maior que remoto, verificar lote aceito sem persistencia no ERP.
5. Se local menor que remoto, verificar reenvio antigo ou janela incorreta.

## Incidente: autenticacao ERP

Sinais:

- HTTP `401` ou `403`;
- heartbeat falha;
- dispatch entra em retry.

Passos:

1. Confirmar token provisionado no ERP.
2. Confirmar variavel de ambiente de maquina:

```powershell
[Environment]::GetEnvironmentVariable("PDV_SYNC_ERP_ACCESS_TOKEN", "Machine")
```

3. Confirmar certificado cliente quando mTLS estiver habilitado.
4. Rotacionar token com `provision_sync_agent --rotate-token` se houver suspeita de vazamento.

## Incidente: ativacao pos-instalacao

Sinais:

- dashboard mostra `not_provisioned`;
- `/setup/activate` retorna erro;
- sincronizacao nao inicia apos instalacao.

Passos:

1. Confirmar que a URL do ERP esta correta.
2. Confirmar que a URL usa HTTPS fora de `localhost`/`127.0.0.1`.
3. Gerar novo codigo de ativacao no ERP se o codigo expirou ou ja foi usado.
4. Nao registrar o codigo de ativacao em chamado ou log.
5. Confirmar que `Provisioning:ProtectedFile` aponta para caminho absoluto no
   servico instalado.
6. Confirmar `/status`:

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status
```

## Incidente: permissao read-only Arpa

Sinais:

- `runtime_status=degraded`;
- `last_error` contem `permission denied for relation produtos` ou
  `permission denied for relation clientes`;
- collector nao consegue ler `sync_export`.

Passos:

1. Nao trocar o SyncAgent para usuario `postgres`.
2. Revalidar permissoes do usuario runtime:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-runtime-read-only>"
.\infra\windows\test-arpa-readonly-permissions.ps1 `
  -PostgresHost 192.168.0.4 `
  -DatabaseName anapolis `
  -DatabaseUser sync_agent_anapolis_ro
```

3. Se autorizado pelo DBA, reaplicar somente DDL/grants:

```powershell
.\infra\windows\prepare-arpa-anapolis-pilot.ps1 `
  -DbaUser postgres `
  -AllowEmptyDbaPassword `
  -ConfirmApply
```

4. Validar exportacao:

```powershell
.\infra\windows\test-arpa-export-preflight.ps1 `
  -PostgresHost 192.168.0.4 `
  -DatabaseName anapolis `
  -DatabaseUser sync_agent_anapolis_ro
```

5. Acionar ciclo manual e confirmar `runtime_status=idle`.

## Evidencias obrigatorias

Coletar antes de qualquer correcao destrutiva:

- print ou JSON de `/status`;
- `instance_id` e `tenant_id`;
- ultimas 5 linhas de `sync_agent.reconciliation_runs`;
- contagem por status de `sync_agent.outbox_events`;
- amostra de `sync_agent.dead_letter_events`;
- status central no ERP;
- horario local e timezone do banco.

## Regras de seguranca

- Nao compartilhar Bearer token em chamados.
- Nao copiar PFX/certificado para canais de suporte.
- Nao remover banco local sem backup e autorizacao.
- Nao fazer `DELETE` em outbox, DLQ ou reconciliation sem plano de recuperacao.
