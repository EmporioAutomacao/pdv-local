# Sync Agent - Verificacao Rigida

Data: 02/06/2026

## Objetivo

Conferir de forma rigida o que foi entregue ate agora antes de iniciar a
proxima etapa.

Repositorios verificados:

- `../sync`
- `../erp`
- `../pdv-local`

## Resultado executivo

Status: aprovado tecnicamente para seguir.

Nao foram encontrados bloqueios tecnicos nos contratos, ERP, SyncAgent,
instalador ou piloto Anapolis.

Faltam 0 gates tecnicos da fase de ativacao pos-instalacao.

## Contratos Sync

Repositorio: `../sync`

Validacoes:

- `README.md`: versao `1.4.0` presente;
- `CHANGELOG.md`: versao `1.4.0` presente;
- `openapi/erp-api-v1.yaml`: versao `1.4.0` presente;
- endpoint `POST /v1/sync/activation:validate` presente;
- endpoint `POST /v1/sync/activation:complete` presente;
- endpoint `POST /v1/sync/agents/{instanceId}/token:refresh` presente;
- 10 exemplos JSON parseados com sucesso.

Resultado: OK.

## ERP Sync API

Repositorio: `../erp`

Comandos executados:

```powershell
.\venv\Scripts\python.exe manage.py check
.\venv\Scripts\python.exe manage.py makemigrations sync_api --check --dry-run
.\venv\Scripts\python.exe manage.py test sync_api --keepdb
```

Resultado:

- migrations `sync_api`: sem pendencias;
- testes `sync_api`: 28 testes OK;
- `manage.py check`: OK com aviso conhecido do CKEditor.

Observacao:

- o aviso do CKEditor nao pertence ao SyncAgent, mas deve ser tratado em trilha
  propria de seguranca do ERP.

Resultado: OK.

## SyncAgent e Tray

Repositorio: `../pdv-local`

Comandos executados:

```powershell
dotnet build src\sync-agent\SyncAgent.csproj -c Release --no-incremental -o artifacts\verification\sync-agent
dotnet build src\sync-agent-tray\SyncAgent.Tray.csproj -c Release --no-incremental -o artifacts\verification\sync-agent-tray
```

Resultado:

- SyncAgent build OK, 0 avisos, 0 erros;
- SyncAgent.Tray build OK, 0 avisos, 0 erros.

Resultado: OK.

## Scripts Windows

Validacao:

- parse sintatico de 18 scripts PowerShell em `infra/windows`.

Resultado: OK.

## Instalador

Comando executado:

```powershell
.\artifacts\sync-agent-installer\infra\install-sync-agent.ps1 -ValidateOnly
```

Resultado:

- layout do pacote OK;
- payload SyncAgent presente;
- payload Tray presente;
- scripts essenciais presentes.

Resultado: OK.

## Release readiness

Comando executado:

```powershell
.\infra\windows\test-sync-agent-release-readiness.ps1 `
  -ErpApiBaseUrl "http://127.0.0.1:8000" `
  -InstanceId "anapolis-local-test-01" `
  -AccessTokenFile "..\erp\.secrets\sync-agent\anapolis-local-test-01.token" `
  -EvidenceOutput ".\artifacts\sync-agent-release-readiness-anapolis.json"
```

Resultado:

- readiness OK;
- 14 checks OK;
- 0 checks falhos;
- evidencia gerada em:
  `artifacts/sync-agent-release-readiness-anapolis.json`.

Resultado: OK.

## Piloto Anapolis

Comando executado:

```powershell
.\infra\windows\test-anapolis-pilot-health.ps1
```

Resultado:

- ERP connectivity: `online`;
- ERP queue: `0`;
- ERP rejected 24h: `0`;
- ERP reconciliation matched: `true`;
- local runtime: `idle`;
- local pending: `0`;
- local dead-letter: `0`;
- local reconciliation: `completed`;
- `/logs`: HTTP 200.

Resultado: OK.

## Arpa read-only

Comandos executados:

```powershell
.\infra\windows\test-arpa-readonly-permissions.ps1 `
  -PostgresHost 192.168.0.4 `
  -DatabaseName anapolis `
  -DatabaseUser sync_agent_anapolis_ro

.\infra\windows\test-arpa-export-preflight.ps1 `
  -PostgresHost 192.168.0.4 `
  -DatabaseName anapolis `
  -DatabaseUser sync_agent_anapolis_ro
```

Resultado:

- usuario runtime Arpa nao e superuser;
- usuario runtime Arpa nao tem `CREATE`;
- usuario runtime Arpa nao tem `INSERT`;
- usuario runtime Arpa nao tem `UPDATE`;
- usuario runtime Arpa nao tem `DELETE`;
- usuario runtime Arpa nao tem `TRUNCATE`;
- usuario runtime Arpa tem `SELECT` nas views aprovadas;
- `sync_export.produtos`: OK;
- `sync_export.clientes`: OK.

Resultado: OK.

## Seguranca

Verificacoes:

- fluxo runtime permanece `Arpa -> SyncAgent -> ERP`;
- nenhuma escrita runtime no Arpa encontrada no codigo do SyncAgent;
- ativacao e refresh bloqueiam HTTP fora de `localhost`/`127.0.0.1`;
- credenciais tecnicas ficam em DPAPI `LocalMachine`;
- senha Arpa runtime usa DPAPI no piloto;
- `refresh_token` e `activation_code` aparecem apenas como nomes de campos e
  contratos, nao como valores reais;
- nenhum Bearer token real encontrado em `src`, `docs` ou `infra`;
- defaults `pdv_sync/pdv_sync` aparecem apenas como credencial local de
  desenvolvimento.

Resultado: OK.

## Bloqueios encontrados durante a fase e resolvidos

1. `permission denied for relation produtos` no Arpa Anapolis.

Resolucao:

- reaplicada preparacao controlada via DBA apenas para DDL/grants;
- runtime segue usando `sync_agent_anapolis_ro`;
- read-only validado novamente.

2. Build padrao travado por processo SyncAgent em execucao.

Resolucao:

- builds de verificacao usam output isolado em `artifacts/verification`;
- pacote final validado separadamente.

## Pendencias externas

Estas pendencias nao bloqueiam a continuidade tecnica, mas bloqueiam liberacao
final para usuario externo:

- instalacao em maquina limpa como Administrador;
- reinicio real do Windows/servico instalado;
- MSI/EXE assinado ou canal de distribuicao aprovado;
- certificado mTLS real por instalacao no gateway final;
- aviso de seguranca do CKEditor no ERP em trilha separada.

## Conclusao

A fase atual esta tecnicamente consistente e pronta para seguir para a proxima
etapa.
