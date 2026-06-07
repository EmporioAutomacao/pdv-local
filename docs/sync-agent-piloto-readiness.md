# Sync Agent - Readiness do Piloto Tecnico

Data: 30/05/2026

## Objetivo

Definir os gates minimos para liberar um piloto tecnico do Sync Agent em uma
maquina Windows de teste, com PostgreSQL local e ERP de homologacao.

Este documento nao substitui o contrato em `../sync`; ele amarra a instalacao,
provisionamento, smoke test, rollback e evidencias operacionais do piloto.

## Escopo do piloto

Incluido:

- instalacao Windows do `SyncAgent` como servico;
- app de bandeja `SyncAgent.Tray`;
- PostgreSQL 17 local com pgvector 0.8.0;
- timezone padrao do banco em `America/Sao_Paulo`;
- heartbeat remoto para o ERP;
- envio de outbox para `POST /v1/sync/events:batch`;
- reconciliacao remota em `POST /v1/sync/reconciliation:summary`;
- consulta central em `GET /v1/sync/agents/{instanceId}/status`;
- runbook de incidentes para suporte.
- preparacao do coletor Arpa read-only por views `sync_export`.

Fora do piloto:

- escrita de volta no Arpa;
- emissao fiscal pelo sincronizador;
- uso de Docker Desktop no cliente final;
- uma instalacao atendendo multiplos tenants.

## Resumo de prontidao

| Gate | Status | Observacao |
|---|---|---|
| Contrato versionado | OK | `../sync` em `1.4.0` com OpenAPI, exemplos e changelog. |
| Compatibilidade basica | OK | Mudancas adicionadas sem quebra conhecida para v1. |
| API ERP | OK tecnico | Endpoints de ingestao, heartbeat, status e reconciliacao implementados e testados. |
| SyncAgent local | OK tecnico | Worker, dashboard local, tray, dispatcher, heartbeat e reconciliacao implementados. |
| Banco local | OK tecnico | PostgreSQL 17 + pgvector 0.8.0 + schema `sync_agent`. |
| Runbook | OK | `docs/sync-agent-runbook-incidentes.md`. |
| Smoke test | OK | `infra/windows/test-sync-agent-smoke.ps1`. |
| Pacote Windows | OK tecnico | `install-sync-agent.ps1 -ValidateOnly` validado no pacote gerado. |
| MSI/EXE assinado | Pendente | Necessario antes de piloto com usuario final. |
| Piloto com dados reais Arpa | OK tecnico | Anapolis validado com produtos/clientes reais e usuario read-only. |
| Certificados finais/mTLS | Pendente externo | Depende do gateway/proxy/certificados do ambiente alvo. |

## Piloto tecnico local provisionado

Validado em 30/05/2026 com ERP local em `http://127.0.0.1:8000`.

Identidade:

```text
tenant_id: piloto-homologacao
instance_id: piloto-win-01
```

Resultado:

- SyncAgent iniciado com a identidade `piloto-win-01`;
- token lido de `.secrets/sync-agent/piloto-win-01.token`, sem exposicao em log;
- heartbeat remoto enviado com sucesso;
- reconciliacao remota retornou `matched=true`;
- status central do ERP retornou `connectivity=online`;
- fila local `pending=0`;
- dead-letter local `dead_letter=0`.
- pipeline com fixture Arpa validado para 1 `produto` e 1 `cliente`:
  collector, normalizacao, outbox, dispatcher, ERP e reconciliacao.

Smoke test executado:

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 `
  -ErpApiBaseUrl "http://127.0.0.1:8000" `
  -InstanceId "piloto-win-01" `
  -AccessToken "<token-do-cofre-local>"
```

## Pre-requisitos

Na maquina do piloto:

- Windows com permissao de Administrador para instalacao;
- PostgreSQL 17 instalado;
- pgvector 0.8.0 disponivel;
- `psql` no `PATH` ou caminho informado ao instalador;
- .NET 8 Runtime quando o pacote for publicado como `--self-contained false`;
- conectividade HTTPS com o ERP de homologacao;
- token emitido pelo ERP para o par `tenant_id` + `instance_id`;
- certificado cliente instalado quando mTLS estiver habilitado;
- acesso read-only ao banco Arpa quando o coletor estiver habilitado.
- views Arpa `sync_export.produtos` e `sync_export.clientes` validadas pelo
  preflight.
- permissoes read-only do usuario Arpa validadas por
  `infra/windows/test-arpa-readonly-permissions.ps1`.

No ERP:

- migrations aplicadas para `sync_api`;
- instalacao provisionada com `provision_sync_agent`;
- endpoint publico configurado;
- clocks sincronizados com tolerancia operacional aceitavel.

Guia detalhado do lado ERP:

```text
../erp/docs/infra/sync-api-piloto-homologacao.md
```

## Checklist de execucao

Valores padrao do piloto local inicial:

```text
tenant_id: piloto-homologacao
instance_id: piloto-win-01
token file no ERP: .secrets/sync-agent/piloto-win-01.token
```

Conexao Arpa escolhida para o primeiro piloto real:

```text
Anapolis
ArpaControlConexao id: 1
banco: anapolis
runbook: docs/arpa-piloto-anapolis.md
```

1. Confirmar contrato:

```powershell
Select-String -Path D:\GitHub\sync\README.md -Pattern "1.4.0"
Select-String -Path D:\GitHub\sync\openapi\erp-api-v1.yaml -Pattern "/v1/sync"
```

2. Publicar artefatos:

```powershell
dotnet publish src/sync-agent/SyncAgent.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\sync-agent\win-x64
dotnet publish src/sync-agent-tray/SyncAgent.Tray.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\sync-agent-tray\win-x64
.\infra\windows\build-sync-agent-package.ps1 -SkipPublish
```

Validar layout do pacote antes de copiar para a maquina alvo:

```powershell
.\artifacts\sync-agent-installer\infra\install-sync-agent.ps1 -ValidateOnly
```

Esse comando nao exige Administrador e nao altera a maquina. Ele valida se o
pacote contem payload do `SyncAgent`, payload do tray e scripts essenciais.

3. Provisionar instalacao no ERP:

```powershell
.\scripts\dev\provision-sync-pilot.ps1 `
  -TenantId "<tenant>" `
  -InstanceId "<instance>"
```

Registrar o token apenas no cofre/variavel de ambiente da maquina. O wrapper
grava o token em `.secrets/sync-agent/<instance>.token`, caminho ignorado pelo
Git. Nao colar o token em chamados, prints ou logs.

4. Instalar em PowerShell como Administrador:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "<instance>" `
  -ErpTenantId "<tenant>" `
  -ErpApiBaseUrl "https://erp-homologacao.exemplo.com" `
  -AccessToken "<token>"
```

5. Validar servico:

```powershell
Get-Service "PDV Local Sync Agent"
Invoke-RestMethod http://127.0.0.1:47891/status
```

6. Executar smoke test local:

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 -SkipErp
```

7. Executar smoke test com ERP:

```powershell
.\infra\windows\test-sync-agent-smoke.ps1 `
  -ErpApiBaseUrl "https://erp-homologacao.exemplo.com" `
  -InstanceId "<instance>" `
  -AccessToken "<token>"
```

8. Abrir o dashboard local:

```text
http://127.0.0.1:47891/
```

9. Confirmar status central no ERP:

```text
GET /v1/sync/agents/{instanceId}/status
```

10. Registrar evidencias:

- JSON de `/status` local sem segredos;
- status central do ERP;
- horario local da maquina;
- status do servico Windows;
- versao instalada do agente;
- contagem de outbox, dead-letter e ultima reconciliacao.
- evidencia de que o usuario Arpa nao possui permissao de escrita.

11. Preparar coletor Arpa:

```text
docs/arpa-collector-piloto.md
```

## Criterios GO/NO-GO

GO somente se todos forem verdadeiros:

- servico Windows em execucao;
- dashboard local responde em `127.0.0.1`;
- `POST /sync-now` responde `202` ou `409`;
- heartbeat remoto bem-sucedido;
- ERP retorna `connectivity=online` para a instalacao;
- sem crescimento continuo de `pending_outbox_events`;
- `dead_letter_events=0` no inicio do piloto;
- ultima reconciliacao remota com `remote_matched=true` apos janela de teste;
- rollback testado em ambiente de homologacao.

NO-GO se qualquer item ocorrer:

- token ou certificado ausente/invalido;
- endpoint ERP inacessivel;
- migracoes ERP pendentes;
- contrato divergente da versao `1.4.0`;
- dead-letter com payload de negocio real sem causa conhecida;
- reconciliacao divergente sem explicacao;
- instalador nao assinado para usuario final;
- ausencia de rollback validado.

## Rollback

Rollback padrao preserva o banco local.

```powershell
.\infra\windows\uninstall-sync-agent.ps1
```

Validar apos rollback:

```powershell
Get-Service "PDV Local Sync Agent" -ErrorAction SilentlyContinue
Test-Path "C:\Program Files\PDVLocal"
```

O banco `pdv_sync` deve ser preservado ate a engenharia autorizar remocao ou
backup definitivo.

## Bloqueios conhecidos para piloto real

- gerar MSI/EXE assinado ou definir canal de distribuicao aprovado;
- receber endpoint, tenant, instance e token do ERP de homologacao;
- definir politica final de certificado cliente/mTLS;
- executar instalacao limpa como Administrador em maquina alvo;
- validar tempo de fila e reconciliacao durante uma janela operacional real.

## Proxima fase

A proxima fase funcional e a ativacao pos-instalacao, documentada em:

```text
docs/sync-agent-ativacao-pos-instalacao.md
```

Objetivo: permitir que o cliente instale o SyncAgent e depois conecte a
instalacao ao ERP pelo dashboard/tray local, usando URL do ERP e codigo de
ativacao. O agente nao deve armazenar usuario e senha do ERP.

## Validacao local do pacote

Executada em 01/06/2026:

- scripts PowerShell do pacote parseados com sucesso;
- `dotnet build pdv-local.sln --no-incremental` passou com 0 avisos e 0 erros;
- `build-sync-agent-package.ps1 -SkipPublish` reconstruiu
  `artifacts/sync-agent-installer`;
- `artifacts/sync-agent-installer/infra/install-sync-agent.ps1 -ValidateOnly`
  passou, confirmando layout do pacote e payloads;
- instalacao real como Windows Service nao foi executada nesta sessao porque o
  PowerShell atual nao esta elevado como Administrador.

## Homologacao tecnica Gate 5

Executada em 02/06/2026 com o piloto Anapolis.

Comando padrao:

```powershell
.\infra\windows\test-sync-agent-release-readiness.ps1 `
  -ErpApiBaseUrl "http://127.0.0.1:8000" `
  -InstanceId "anapolis-local-test-01" `
  -AccessTokenFile "..\erp\.secrets\sync-agent\anapolis-local-test-01.token" `
  -EvidenceOutput ".\artifacts\sync-agent-release-readiness-anapolis.json"
```

Resultado:

- readiness: OK;
- evidencia: `artifacts/sync-agent-release-readiness-anapolis.json`;
- pacote: OK;
- contrato `1.4.0`: OK;
- runtime local: `idle`;
- instalacao: `provisioned=true`;
- fila local: `0`;
- dead-letter: `0`;
- `/logs`: HTTP 200;
- `/setup`: `Cache-Control=no-store`;
- ERP: `connectivity=online`;
- ERP queue: `0`;
- ERP rejected 24h: `0`;
- reconciliacao ERP: `matched=true`.

Bloqueio detectado e corrigido durante homologacao:

- `42501: permission denied for relation produtos` no usuario runtime Arpa;
- corrigido reaplicando `prepare-arpa-anapolis-pilot.ps1` com DBA `postgres`
  sem senha apenas para DDL/grants;
- validado novamente com:
  - `test-arpa-readonly-permissions.ps1`;
  - `test-arpa-export-preflight.ps1`.

Pendencias externas para liberacao com usuario final:

- instalacao limpa como Administrador em maquina alvo;
- reinicio real do Windows/servico instalado;
- MSI/EXE assinado ou canal de distribuicao aprovado;
- certificado mTLS real por instalacao no gateway final.
