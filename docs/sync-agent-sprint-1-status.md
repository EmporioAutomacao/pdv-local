# Sync Agent - Sprint 1 Status

Data: 30/05/2026

## Objetivo

Criar a base tecnica do `SyncAgent` local para sincronizacao offline-first entre
PDV/Arpa local e ERP em nuvem, usando os contratos versionados em `../sync`.

## Decisoes tomadas

1. O codigo de implementacao fica em `../pdv-local`, nao em `../sync`.
2. `../sync` continua sendo fonte de verdade para OpenAPI, schemas, exemplos e changelog.
3. Runtime principal do agente: .NET Worker.
4. Banco local: PostgreSQL 17 com pgvector 0.8.0, alinhado ao ERP.
5. Docker Compose e apenas para desenvolvimento.
6. Cliente final deve usar instalacao Windows com PostgreSQL local, `SyncAgent` como Windows Service e `SyncAgent.Tray` na bandeja.
7. O agente nao descobre o cliente sozinho; recebe `InstanceId`, `ErpTenantId` e `ErpApiBaseUrl` por provisionamento.
8. Credenciais ERP ficam centralizadas em `ErpSecurity`, sem logar token ou senha.
9. Senha runtime do Arpa em Windows deve usar arquivo protegido por DPAPI
   LocalMachine via `ArpaCollector:PasswordProtectedFile`.

## Entregue

### Projetos

- `src/sync-agent/SyncAgent.csproj`
- `src/sync-agent-tray/SyncAgent.Tray.csproj`
- `pdv-local.sln`

### Banco local

Ambiente de desenvolvimento:

- Imagem: `pgvector/pgvector:0.8.0-pg17`
- Porta local: `55432`
- Banco: `pdv_sync`
- Usuario: `pdv_sync`
- Timezone padrao: `America/Sao_Paulo` (Brasilia, UTC-3)

Schema criado:

- `sync_agent.outbox_events`
- `sync_agent.dispatch_attempts`
- `sync_agent.inbox_events`
- `sync_agent.agent_state`
- `sync_agent.reconciliation_runs`
- `sync_agent.dead_letter_events`

Extensao:

- `vector` versao `0.8.0`

Observacao: os campos do contrato com sufixo `_utc` continuam representando
instantes UTC no payload de integracao. A timezone `America/Sao_Paulo` define o
padrao de sessao/exibicao/defaults do PostgreSQL local.

### SyncAgent

O Worker ja faz:

- sobe como console em desenvolvimento;
- esta preparado para Windows Service com nome `PDV Local Sync Agent`;
- valida configuracao obrigatoria;
- registra identidade local em `sync_agent.agent_state` com chave `agent.identity`;
- le metricas basicas da fila local;
- expoe API local em `127.0.0.1`;
- aceita disparo manual de ciclo via `POST /sync-now`;
- diferencia ciclo `startup`, `scheduled` e `manual`;
- mantem estado runtime para diagnostico;
- executa coletor Arpa configuravel quando habilitado;
- executa dispatcher ERP configuravel quando habilitado;
- envia heartbeat remoto configuravel quando habilitado;
- resolve Bearer token e certificado cliente por `ErpSecurity`.

Configuracao atual:

```json
{
  "SyncAgent": {
    "InstanceId": "local-dev-agent-01",
    "ErpTenantId": "local-dev",
    "ErpApiBaseUrl": "https://localhost:5001",
    "AgentVersion": "0.1.0-dev",
    "PollingIntervalSeconds": 30,
    "LocalStatusPort": 47891
  },
  "ErpDispatcher": {
    "Enabled": false,
    "BatchSize": 50,
    "TimeoutSeconds": 30,
    "MaxAttempts": 8,
    "InitialBackoffSeconds": 60,
    "MaxBackoffSeconds": 3600
  },
  "ErpHeartbeat": {
    "Enabled": false,
    "TimeoutSeconds": 15
  },
  "ErpSecurity": {
    "AccessTokenEnvironmentVariable": "PDV_SYNC_ERP_ACCESS_TOKEN",
    "AccessToken": "",
    "ClientCertificateThumbprint": "",
    "ClientCertificateStoreName": "My",
    "ClientCertificateStoreLocation": "LocalMachine",
    "ClientCertificatePath": "",
    "ClientCertificatePasswordEnvironmentVariable": "PDV_SYNC_ERP_CERT_PASSWORD",
    "ClientCertificatePassword": "",
    "RequireBearerToken": true,
    "RequireMutualTls": true
  }
}
```

### API local

Restrita a `127.0.0.1`.

```text
GET  http://127.0.0.1:47891/
GET  http://127.0.0.1:47891/help
GET  http://127.0.0.1:47891/status
POST http://127.0.0.1:47891/sync-now
```

`GET /` exibe dashboard HTML local. `GET /help` exibe ajuda operacional local.
`GET /status` retorna JSON tecnico.

Resposta esperada de `/status`:

```json
{
  "status": "ok",
  "instance_id": "local-dev-agent-01",
  "erp_tenant_id": "local-dev",
  "agent_version": "0.1.0-dev",
  "database": "pdv_sync",
  "pgvector_version": "0.8.0",
  "pending_outbox_events": 0,
  "dead_letter_events": 0,
  "oldest_pending_age_seconds": 0,
  "last_heartbeat_at_utc": null,
  "last_heartbeat_succeeded": null,
  "last_heartbeat_connectivity": null,
  "runtime_status": "idle",
  "last_cycle_trigger": "manual",
  "last_error": null
}
```

### Outbox

`LocalSyncStore` ja possui base para gravar eventos canonicos:

- valida `source_system`, `entity_type` e `event_type` conforme contrato v1;
- calcula `payload_hash` em formato `sha256:<hex>`;
- serializa JSON com ordenacao estavel de propriedades;
- grava com `ON CONFLICT (event_id) DO NOTHING`.

Valores aceitos pelo contrato atual:

- `source_system`: `arpa`
- `entity_type`: `cliente`, `produto`, `estoque`, `venda`, `financeiro`
- `event_type`: `upsert`, `delete_logico`, `status_update`

### Coletor Arpa

O coletor Arpa foi implementado como adaptador configuravel e fica
desabilitado por padrao.

Cada adaptador configurado executa uma query read-only no banco Arpa e espera
as colunas:

```text
entity_key
occurred_at_utc
payload_json
trace_id opcional
```

Parametros obrigatorios da query:

```text
@watermark_utc
@limit
```

O coletor:

- usa watermark por entidade em `sync_agent.agent_state`;
- cria `event_id` deterministico;
- gera eventos `upsert`;
- grava no outbox local;
- respeita os tipos permitidos pelo contrato v1.

### Normalizadores

Normalizador implementado:

- `produto`
- `cliente`
- `estoque`
- `venda`
- `financeiro`

O payload de `produto` passa a ser normalizado antes do outbox, com estes campos
canonicos quando disponiveis:

- `codigo_arpa`
- `nome`
- `codigo_fabrica`
- `ncm`
- `codigo_barras`
- `ativo`

O payload de `cliente` passa a ser normalizado com estes campos canonicos quando
disponiveis:

- `codigo_arpa`
- `nome`
- `documento`
- `email`
- `telefone`
- `ativo`

O payload de `estoque` passa a ser normalizado com estes campos canonicos quando
disponiveis:

- `codigo_produto_arpa`
- `loja_codigo`
- `quantidade`
- `minimo`
- `maximo`
- `local`

Normalizadores ainda pendentes:

- nenhum para o contrato v1 atual.

### Dispatcher ERP

O dispatcher HTTPS foi implementado para enviar lotes do outbox ao endpoint
contratado:

```text
POST /v1/sync/events:batch
```

Ele fica desabilitado por padrao porque o ambiente local ainda nao tem API ERP
de ingestao rodando em `https://localhost:5001`.

Quando habilitado:

- seleciona eventos `pending` respeitando `next_attempt_at_utc`;
- usa `FOR UPDATE SKIP LOCKED` para evitar concorrencia entre ciclos;
- marca eventos como `in_flight`;
- registra tentativa em `sync_agent.dispatch_attempts`;
- monta `EventsBatchRequest` conforme OpenAPI/schema em `../sync`;
- envia para `/v1/sync/events:batch`;
- em HTTP `202`, marca eventos aceitos como `accepted`;
- em rejeicoes por evento, copia para `sync_agent.dead_letter_events` e marca
  outbox como `dead_letter`;
- em falha transitoria, retorna eventos para `pending` com backoff exponencial;
- quando `MaxAttempts` e atingido, copia para `sync_agent.dead_letter_events`
  e marca outbox como `dead_letter`.

Classificacao atual:

- `400`, `404`, `409`, `422`: falha permanente, vai direto para dead-letter.
- `401`, `403`, `429`, `5xx` e falhas de transporte: falha transitoria,
  sujeita a retry/backoff ate `MaxAttempts`.

Baseline de seguranca aplicado nesta etapa:

- HTTPS obrigatorio fora de `localhost`/`127.0.0.1`;
- suporte configuravel a Bearer token;
- suporte configuravel a certificado cliente para mTLS;
- logs nao imprimem token, senha de certificado ou payload sensivel.

As credenciais sao resolvidas por `ErpSecurity`.

### Heartbeat ERP

O heartbeat remoto foi implementado para publicar status operacional no
endpoint contratado:

```text
POST /v1/sync/agents/{instanceId}/heartbeat
```

Ele fica desabilitado por padrao ate existir API ERP disponivel no ambiente
local.

Quando habilitado:

- monta `HeartbeatRequest` conforme OpenAPI em `../sync`;
- envia `timestamp_utc`, `agent_version`, `queue_size`,
  `oldest_pending_age_seconds` e `connectivity`;
- usa `InstanceId` no path do endpoint;
- salva o ultimo resultado em `sync_agent.agent_state` com chave
  `agent.heartbeat`;
- expoe o ultimo heartbeat em `GET /status`, dashboard local e tray.

Baseline de seguranca aplicado:

- HTTPS obrigatorio fora de `localhost`/`127.0.0.1`;
- suporte configuravel a Bearer token;
- suporte configuravel a certificado cliente para mTLS;
- logs nao imprimem token, senha de certificado ou payload sensivel.

### Autenticacao e provisionamento

Foi criada a base operacional de autenticacao/provisionamento do agente:

- secao central `ErpSecurity`;
- Bearer token resolvido preferencialmente por variavel de ambiente
  `PDV_SYNC_ERP_ACCESS_TOKEN`;
- certificado cliente resolvido por thumbprint no Windows Certificate Store;
- fallback tecnico para PFX local via `ClientCertificatePath`;
- senha de PFX resolvida preferencialmente por variavel de ambiente
  `PDV_SYNC_ERP_CERT_PASSWORD`;
- exigencia de HTTPS fora de `localhost`/`127.0.0.1`;
- `RequireBearerToken` e `RequireMutualTls` ativos por padrao para endpoint
  remoto;
- dispatcher e heartbeat usam o mesmo provedor de credenciais;
- script Windows inicial:
  `infra/windows/provision-sync-agent-security.ps1`.
- senha read-only do Arpa pode ser lida por `PasswordProtectedFile` protegido
  com Windows DPAPI LocalMachine; `PasswordEnvironmentVariable`, `PasswordFile`
  e `PasswordProtectedFile` sao mutuamente exclusivos.

Ainda falta a emissao real dessas credenciais pelo ERP/Control Plane, que entra
na etapa de API/processamento do ERP.

### ERP Sync API

No repositorio `../erp`, foi criado o app Django `sync_api` com a primeira
versao da API de ingestao contratada:

```text
POST /v1/sync/events:batch
POST /v1/sync/agents/{instanceId}/heartbeat
```

Entregue no ERP:

- modelos `SyncInstallation`, `SyncEventBatch`, `SyncReceivedEvent` e
  `SyncAgentHeartbeat`;
- autenticacao Bearer token com armazenamento apenas do hash SHA-256;
- comando `provision_sync_agent` para emitir token de instalacao uma unica vez;
- idempotencia por `event_id` e `payload_hash`;
- rejeicao de `event_id` repetido com hash diferente;
- validacao de `source_instance_id` contra a instalacao autenticada;
- persistencia do heartbeat operacional por instalacao;
- admin basico para auditoria;
- testes automatizados do fluxo de lote, idempotencia, rejeicao e heartbeat.

Processamento de dominio ERP atual:

- `produto`: cria/atualiza `produtos.Produto` por `codigo_arpa`;
- `cliente`: cria/atualiza `clientes.Cliente` por documento/CNPJ-CPF;
- `estoque`: cria/atualiza `produtos.Estoque` por produto Arpa + loja;
- `venda`: cria/atualiza `vendas.Venda` por `origem=arpa` + `codigo_externo`
  e substitui os itens pelo payload recebido;
- `financeiro`: cria/atualiza `financeiro.TituloReceber` por origem Arpa +
  `titulo_externo_id`.

### Reconciliacao local

O SyncAgent grava uma execucao de reconciliacao local a cada ciclo em
`sync_agent.reconciliation_runs`.

Resumo atual:

- janela: ultimas 24 horas;
- contadores de eventos capturados, aceitos, rejeitados, pendentes, in-flight e
  dead-letter;
- quebra por `entity_type` + `status`;
- top motivos de dead-letter na janela.

O resultado aparece em `GET /status` nos campos:

- `last_reconciliation_id`;
- `last_reconciliation_status`;
- `last_reconciliation_completed_at_utc`;
- `last_reconciliation_summary`.

Dashboard local e tray exibem o estado da ultima reconciliacao.

### Reconciliacao remota

Foi adicionado o cliente remoto de reconciliacao:

```text
POST /v1/sync/reconciliation:summary
```

Configuracao:

```json
{
  "ErpReconciliation": {
    "Enabled": false,
    "TimeoutSeconds": 30
  }
}
```

Quando habilitado, o SyncAgent envia ao ERP o resumo por entidade da janela de
24 horas. O ERP compara contadores e `aggregate_hash` com os eventos recebidos
em `sync_api.SyncReceivedEvent`.

O resultado remoto e salvo no `summary` da reconciliacao local:

- `remote_matched`;
- `remote_response`.

### SyncAgent.Tray

O app de bandeja ja:

- compila como WinForms;
- consulta `GET /status`;
- aciona `POST /sync-now`;
- abre a ajuda local em `GET /help`;
- exibe status, tenant, instalacao e pendencias;
- copia ID da instalacao;
- nao executa sincronizacao diretamente.

### Instalador Windows

Foi criada a base de instalacao Windows sem dependencia de Docker Desktop:

- `infra/windows/build-sync-agent-package.ps1`: gera pacote em
  `artifacts/sync-agent-installer`;
- `infra/windows/install-sync-agent.ps1`: instala/atualiza o agente;
- `infra/windows/uninstall-sync-agent.ps1`: remove o servico preservando banco
  por padrao;
- `infra/windows/bootstrap-sync-agent-db.ps1`: aplica timezone, pgvector e
  schema local;
- `infra/windows/provision-sync-agent-security.ps1`: provisiona token em
  variavel de ambiente de maquina e importa PFX quando informado.

O instalador:

- valida execucao como Administrador;
- aplica bootstrap do PostgreSQL quando `-SkipDatabaseBootstrap` nao for usado;
- copia `SyncAgent` e `SyncAgent.Tray` para `C:\Program Files\PDVLocal`;
- grava `appsettings.json` provisionado;
- instala/atualiza Windows Service `PDV Local Sync Agent`;
- configura restart automatico em falha;
- cria atalho do tray na inicializacao do Windows;
- nao remove banco local na desinstalacao padrao.

Pre-requisitos do cliente:

- PostgreSQL 17 instalado;
- pgvector 0.8.0 disponivel;
- `psql` no `PATH` ou informado via `-PsqlPath`;
- .NET 8 Runtime quando a publicacao for `--self-contained false`.

## Como validar hoje

Na raiz `D:\GitHub\pdv-local`:

```powershell
docker compose up -d postgres
docker inspect --format='{{.State.Health.Status}}' pdv-local-postgres
docker exec pdv-local-postgres psql -U pdv_sync -d pdv_sync -tAc "SELECT extname, extversion FROM pg_extension WHERE extname = 'vector';"
docker exec pdv-local-postgres psql -U pdv_sync -d pdv_sync -tAc "SELECT table_name FROM information_schema.tables WHERE table_schema = 'sync_agent' ORDER BY table_name;"
dotnet build pdv-local.sln
dotnet run --project src/sync-agent/SyncAgent.csproj
```

Em outro PowerShell:

```powershell
Invoke-RestMethod -Uri "http://127.0.0.1:47891/status" -Method Get
Invoke-RestMethod -Uri "http://127.0.0.1:47891/sync-now" -Method Post
Invoke-RestMethod -Uri "http://127.0.0.1:47891/status" -Method Get
```

Publicacao:

```powershell
dotnet publish src/sync-agent/SyncAgent.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\sync-agent\win-x64
dotnet publish src/sync-agent-tray/SyncAgent.Tray.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\sync-agent-tray\win-x64
```

## Ultima validacao executada

Validado em 30/05/2026:

- `dotnet build pdv-local.sln`: sucesso, 0 avisos, 0 erros.
- `dotnet publish` do `SyncAgent`: sucesso.
- `dotnet publish` do `SyncAgent.Tray`: sucesso.
- `GET /status`: sucesso.
- `POST /sync-now`: sucesso.
- ciclo manual registrado com `last_cycle_trigger = manual`.
- coletor Arpa configuravel: sucesso com fixture local, 1 evento coletado, 1 evento inserido e watermark atualizado.
- normalizador `produto`: sucesso com fixture local, payload canonico gravado no outbox.
- normalizadores `cliente` e `estoque`: sucesso com fixture local, payload canonico gravado no outbox.
- normalizadores `venda` e `financeiro`: sucesso com fixture local, payload canonico gravado no outbox.
- dispatcher ERP: envio validado contra ERP falso local em
  `POST /v1/sync/events:batch`; request gerado conforme contrato v1.
- retry/backoff: validado com ERP falso retornando HTTP 500; evento voltou
  para `pending` com `next_attempt_at_utc` futuro.
- dead-letter: validado com `MaxAttempts=1`; evento foi copiado para
  `sync_agent.dead_letter_events` e outbox ficou `dead_letter`.
- heartbeat ERP: validado contra ERP falso local em
  `POST /v1/sync/agents/local-dev-agent-01/heartbeat`; request gerado conforme
  contrato v1 e estado `agent.heartbeat` salvo no banco.
- autenticacao: validado Bearer token por variavel de ambiente
  `PDV_SYNC_ERP_ACCESS_TOKEN`; ERP falso recebeu header `Authorization`.
- ERP Sync API: `manage.py test sync_api --keepdb` passou com 7 testes.
- ERP Sync API: `manage.py makemigrations sync_api --check --dry-run` sem
  mudancas pendentes.
- instalador Windows: scripts PowerShell parseados com sucesso.
- pacote Windows: `build-sync-agent-package.ps1 -SkipPublish` gerou estrutura
  de instalacao em `artifacts/sync-agent-installer`.
- readiness de piloto: documentado em
  `docs/sync-agent-piloto-readiness.md`.
- smoke test operacional: criado em
  `infra/windows/test-sync-agent-smoke.ps1`.
- piloto tecnico local provisionado:
  `tenant_id=piloto-homologacao`, `instance_id=piloto-win-01`.
- smoke test com ERP local: sucesso, `connectivity=online`,
  `last_reconciliation_matched=true`.
- preparacao do coletor Arpa real: template e preflight criados para views
  read-only `sync_export.produtos` e `sync_export.clientes`.
- fixture local criada para validar preflight/coleta sem depender do Arpa real:
  `infra/arpa/sync-export-dev-fixture.sql`.
- pipeline com fixture Arpa validado: 1 `produto` e 1 `cliente` coletados,
  enviados ao ERP e reconciliados com `matched=true`.
- gerador de SQL candidato para views Arpa criado:
  `infra/windows/new-arpa-sync-export-views-candidate.ps1`.
- exportador de diagnostico de schema Arpa criado:
  `infra/windows/export-arpa-schema-diagnostics.ps1`.
- gerador de views a partir de diagnostico criado:
  `infra/windows/new-arpa-sync-export-views-from-diagnostics.ps1`.
- decisao de piloto para produtos Arpa registrada:
  carga inicial controlada ate existir watermark confiavel.
- script protegido para aplicar views Arpa criado:
  `infra/windows/apply-arpa-sync-export-views.ps1`.
- politica Arpa read-only registrada: o SyncAgent nunca grava no Arpa.
- preflight de permissao read-only criado:
  `infra/windows/test-arpa-readonly-permissions.ps1`.
- conexao do piloto real escolhida: Anapolis (`ArpaControlConexao id=1`,
  banco `anapolis`).
- SQL de views para Anapolis gerado:
  `infra/arpa/sync-export-views-anapolis.initial-load.sql`.
- bloqueio de seguranca identificado: conexao Anapolis atual usa `postgres`
  superuser; nao pode ser usada como runtime do SyncAgent.
- template de usuario read-only Anapolis criado:
  `infra/arpa/sync-export-readonly-user-anapolis.template.sql`.
- script consolidado de preparacao Anapolis criado:
  `infra/windows/prepare-arpa-anapolis-pilot.ps1`.
- script consolidado validado sem aplicar DDL real: sintaxe PowerShell OK e
  execucao sem `-ConfirmApply` bloqueia corretamente.
- coletor Arpa atualizado para aceitar senha read-only fora da connection
  string, via `PasswordFile` ou `PasswordEnvironmentVariable`.
- preparacao real Anapolis executada em `192.168.0.4:5432/anapolis`:
  `sync_export` criado, views aplicadas, usuario `sync_agent_anapolis_ro`
  criado/ajustado, permissoes read-only e preflight validados com sucesso.
- teste real `Arpa -> SyncAgent` executado com dispatcher desligado:
  1.538 produtos e 2.136 clientes coletados para outbox local como `pending`;
  `dead_letter=0`, `pgvector=0.8.0`, dashboard local ativo em
  `http://127.0.0.1:47891/`.
- teste real `SyncAgent -> ERP local` executado para
  `instance_id=anapolis-local-test-01`: 3.674 eventos enviados e aceitos pelo
  ERP (`1.538` produtos, `2.136` clientes), `dead_letter=0`, reconciliacao
  remota final `matched=true`.
- recuperacao de eventos `in_flight` implementada no dispatcher para cobrir
  queda/interrupcao no meio do envio; validada ao reenviar lote final de 174
  eventos com idempotencia no ERP.
- fila pendente apos envio ao ERP local: `0`.
- dashboard local atualizado com guia `Logs` em `/logs`, exibindo ultimos
  envios ao ERP e reconciliacoes gravados no banco local.
- guia `Logs` enriquecida com amostra limitada dos registros afetados por
  envio ao ERP: ate 50 itens por lote, sem payload completo.
- documentado como verificar registros modificados na guia `Logs`: abrir
  `/logs`, localizar `Envio ERP` e conferir `Entidade`, `Chave`, `Evento`,
  `Status`, `Ocorrido em` e `Trace` na coluna `Detalhes`.
- scripts operacionais do piloto Anapolis criados:
  `infra/windows/start-sync-agent-anapolis-pilot.ps1` e
  `infra/windows/test-anapolis-pilot-health.ps1`.
- script de start da Sync API em desenvolvimento criado no ERP:
  `scripts/dev/start-sync-api-dev.ps1`.
- healthcheck do piloto validado com sucesso: ERP `online`, fila ERP `0`,
  eventos recebidos 24h `3674`, rejeitados `0`, reconciliacao `matched=true`,
  logs locais HTTP `200`.
- protecao local de segredo Arpa implementada e validada em 01/06/2026:
  `infra/windows/protect-sync-agent-secret.ps1` gerou
  `.secrets/arpa/anapolis-runtime-password.dpapi`; SyncAgent reiniciado usando
  `ArpaCollector:PasswordProtectedFile`; `dotnet build` passou sem avisos e o
  healthcheck do piloto retornou ERP `online`, fila local `0`, dead-letter `0`,
  reconciliacao `completed` e logs HTTP `200`.
- instalador Windows atualizado para provisionar coletor Arpa opcionalmente:
  `-EnableArpaCollector`, `-ArpaConnectionString`, `-ArpaPassword`,
  `-ArpaCollectorPreset AnapolisInitialLoad` e `-ArpaBatchSize`; a senha e
  gravada como DPAPI em `C:\Program Files\PDVLocal\Secrets` e o
  `appsettings.json` aponta para `ArpaCollector:PasswordProtectedFile`.
- validacao pos-instalador em 01/06/2026: scripts PowerShell parseados com
  sucesso, JSONs parseados com sucesso, `dotnet build pdv-local.sln
  --no-incremental` passou sem avisos, pacote reconstruido e piloto Anapolis
  retornou ERP `online`, fila `0`, dead-letter `0`, reconciliacao
  `matched=true` e logs HTTP `200`.
- layout do pacote de instalacao corrigido para execucao fora do repositorio:
  `install-sync-agent.ps1` agora encontra `payload/SyncAgent` e
  `payload/SyncAgentTray` quando roda dentro de `artifacts/sync-agent-installer`;
  `bootstrap-sync-agent-db.ps1` tambem localiza `infra/postgres/init` no pacote.
- modo `-ValidateOnly` adicionado ao instalador para validar pacote sem exigir
  Administrador; validado em 01/06/2026 no pacote gerado.

## Fora do escopo ja decidido

Nao entra nesta base:

- escrita de volta no Arpa;
- emissao fiscal dentro do sincronizador;
- motor de regras fiscais dentro do agente;
- uma unica instalacao do agente atendendo multiplos tenants;
- Docker Desktop como requisito para cliente final.

## Etapas restantes para V1 utilizavel

Faltam 0 etapas grandes na base tecnica da Sprint 1.

Pendencias de evolucao fora da base tecnica atual:

- empacotamento MSI/EXE assinado;
- execucao de instalacao limpa como Administrador em maquina alvo;
- credenciais e certificados finais de homologacao/producao;
- politica final de distribuicao e assinatura do instalador.

## Proxima etapa recomendada

Iniciar a fase de ativacao pos-instalacao conforme:

```text
docs/sync-agent-ativacao-pos-instalacao.md
```

Essa fase troca o provisionamento tecnico por parametros do instalador por uma
experiencia controlada no dashboard/tray:

```text
instalar -> abrir setup local -> informar URL do ERP + codigo de ativacao -> ativar -> sincronizar
```

Regra principal da nova fase: o SyncAgent nao armazena usuario e senha do ERP.
O login/codigo serve apenas para ativar a instalacao; depois disso, o agente
usa credenciais tecnicas proprias da maquina.
