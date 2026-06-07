# Sync Agent

Worker local do PDV responsavel por executar ciclos de sincronizacao com o ERP.

Status tecnico consolidado: `docs/sync-agent-sprint-1-status.md`.

Manual completo da Sprint 1: `docs/sync-agent-sprint-1-manual.md`.

Runbook de incidentes: `docs/sync-agent-runbook-incidentes.md`.

## Escopo da Sprint 1

- Manter o processo Worker ativo.
- Centralizar configuracao base do agente.
- Preparar o ponto de entrada para ciclos de sincronizacao.
- Usar PostgreSQL como banco local do agente, mantendo alinhamento tecnico com o ERP.
- Criar armazenamento local minimo para outbox, inbox e estado de sincronizacao.

## Banco local

O agente usa PostgreSQL 17 com pgvector, na mesma base do ERP:
`pgvector/pgvector:0.8.0-pg17`.

A extension `vector` e criada no init local em
`infra/postgres/init/01-create-vector-extension.sql`.

O schema inicial do agente fica em
`infra/postgres/init/02-create-sync-agent-schema.sql` e cria:

- `sync_agent.outbox_events`
- `sync_agent.dispatch_attempts`
- `sync_agent.inbox_events`
- `sync_agent.agent_state`
- `sync_agent.reconciliation_runs`
- `sync_agent.dead_letter_events`

A connection string padrao de desenvolvimento fica em
`ConnectionStrings:SyncAgentDb`:

```text
Host=localhost;Port=55432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync
```

Essa credencial e apenas um padrao local para desenvolvimento. Producao deve
injetar a connection string por variavel de ambiente ou secret manager.

## Executar

Ambiente de desenvolvimento com Docker:

Suba o banco local:

```powershell
docker compose up -d postgres
```

Depois execute o Worker:

```powershell
dotnet run --project src/sync-agent/SyncAgent.csproj
```

Para instalacao Windows sem Docker Desktop, consulte
`docs/windows-installation.md`.

O Worker esta preparado para rodar como Windows Service com o nome
`PDV Local Sync Agent`.

## API local

O agente expoe uma API local restrita a `127.0.0.1` para diagnostico e para o
futuro app de bandeja do Windows.

Porta padrao:

```text
SyncAgent:LocalStatusPort = 47891
```

Endpoints iniciais:

```text
GET  http://127.0.0.1:47891/
GET  http://127.0.0.1:47891/help
GET  http://127.0.0.1:47891/logs
GET  http://127.0.0.1:47891/setup
GET  http://127.0.0.1:47891/status
POST http://127.0.0.1:47891/setup/activate
POST http://127.0.0.1:47891/sync-now
```

`GET /` exibe um dashboard HTML local com status, tenant, instalacao,
pendencias, ultimo ciclo, ultimo erro, vendas PDV por status e botao
`Sincronizar agora`.

`GET /logs` exibe as ultimas tarefas persistidas no banco local: envios ao ERP
por lote e reconciliacoes. Para evitar vazamento de dados sensiveis e manter a
tela leve, nao exibe payload completo. Em envios ao ERP, mostra no maximo 50
registros por operacao com `entity_type`, `entity_key`, `event_type`, `status`,
`occurred_at_utc` e `trace_id`.

Para conferir quais registros foram alterados, abra `/logs`, localize uma linha
`Envio ERP` e veja a secao `Amostra dos registros alterados nesta operacao` na
coluna `Detalhes`. A coluna `Entidade` indica o tipo sincronizado e a coluna
`Chave` indica o identificador de origem no Arpa.

`GET /help` exibe a ajuda operacional local para suporte em campo.

`GET /setup` exibe a ativacao pos-instalacao. Com
`Provisioning:Enabled=true`, o agente inicia como `not_provisioned` ate receber
URL do ERP e codigo de ativacao.

`POST /setup/activate` consome o codigo de ativacao no ERP, recebe credenciais
tecnicas da maquina e salva em arquivo protegido por DPAPI `LocalMachine`.

`POST /sync-now` sinaliza o Worker para executar um ciclo manual sem aguardar o
proximo intervalo agendado. Quando `ErpDispatcher:Enabled` estiver ativo, esse
ciclo tambem tenta enviar eventos pendentes para o ERP.

Essa API nao deve ser publicada em interfaces de rede externas.

As chamadas de integracao devem ser implementadas somente a partir dos contratos em `../sync`.

## Identidade do agente

O agente nao descobre sozinho qual cliente do ERP deve atender. Ele recebe uma
configuracao de provisionamento local em `SyncAgent`:

```json
{
  "InstanceId": "local-dev-agent-01",
  "ErpTenantId": "local-dev",
  "ErpApiBaseUrl": "https://localhost:5001",
  "AgentVersion": "0.1.0-dev",
  "PollingIntervalSeconds": 30
}
```

- `InstanceId`: identidade unica da instalacao local, usada em heartbeat e eventos.
- `ErpTenantId`: cliente/tenant no ERP ao qual o agente pertence.
- `ErpApiBaseUrl`: endpoint base da instancia/API do ERP.
- `AgentVersion`: versao operacional reportada pelo agente.

Em producao, esses valores devem vir do processo de provisionamento do ERP ou
Control Plane junto com as credenciais/certificado mTLS.

Para ativacao pos-instalacao, habilite:

```json
{
  "Provisioning": {
    "Enabled": true,
    "ProtectedFile": "C:\\Program Files\\PDVLocal\\Secrets\\sync-agent-provisioning.dpapi",
    "ActivationTimeoutSeconds": 30
  }
}
```

Depois de ativado em `/setup`, a identidade efetiva e as credenciais tecnicas
passam a vir do arquivo protegido. O SyncAgent nao armazena usuario e senha do
ERP.

O Worker persiste a identidade efetiva em `sync_agent.agent_state` com a chave
`agent.identity`, facilitando diagnostico local sem expor segredos.

## Seguranca ERP

Credenciais de integracao ficam centralizadas em `ErpSecurity`:

```json
{
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

Ordem de resolucao:

- Bearer token: variavel de ambiente `PDV_SYNC_ERP_ACCESS_TOKEN`; fallback
  tecnico para `ErpSecurity:AccessToken`.
- Certificado cliente: thumbprint no Windows Certificate Store; fallback
  tecnico para PFX local via `ClientCertificatePath`.
- Senha do PFX: variavel de ambiente `PDV_SYNC_ERP_CERT_PASSWORD`; fallback
  tecnico para `ErpSecurity:ClientCertificatePassword`.

Fora de `localhost`/`127.0.0.1`, o agente exige HTTPS. Por padrao tambem exige
Bearer token e certificado cliente quando dispatcher ou heartbeat estao
habilitados contra endpoint remoto.

Na ativacao pos-instalacao, a URL informada pelo cliente e a URL retornada pelo
ERP sao validadas antes de salvar credenciais. O refresh de token tambem
bloqueia HTTP fora de ambiente local.

A API local usa headers `no-store` e limita o formulario de ativacao para
evitar cache indevido e abuso simples de corpo de request.

O script inicial de provisionamento Windows fica em:

```powershell
.\infra\windows\provision-sync-agent-security.ps1
```

## Dispatcher ERP

O dispatcher envia lotes do outbox para o endpoint contratado em `../sync`:

```text
POST /v1/sync/events:batch
```

## Publisher de vendas PDV

O `PdvSalesPublisher` publica vendas nativas do PDV Local no mesmo outbox usado
pelo dispatcher ERP.

Entrada local:

- `pdv.sales.sync_status = pending_sync`;
- `pdv.sales.status IN ('completed', 'cancelled')`;
- itens em `pdv.sale_items`;
- pagamentos em `pdv.payments`.

Evento gerado:

- `event_id`: `sale_id`, garantindo idempotencia;
- `source_system`: `pdv_local`;
- `entity_type`: `venda`;
- `event_type`: `upsert`;
- `schema_version`: `v1.0`;

Regra de produto no payload:

- `items[].product_id` e o UUID local do produto no PDV;
- `items[].product_erp_id` e enviado quando o produto veio do snapshot ERP ->
  PDV e deve ser usado pelo ERP como chave preferencial de resolucao;
- `items[].product_external_key` permanece para compatibilidade e exibicao, mas
  nao deve ser interpretado antes de `product_erp_id`.
- `payload`: contrato `PdvSalePayloadV1` do repositório `../sync`.

Regra de pagamento no payload:

- contrato `sync` `1.9.0`;
- `payments[].payment_method` permanece como nome operacional exibivel;
- `payments[].payment_species_external_key` e o ID da especie no ERP recebido
  de `GET /v1/sync/pdv/payment-methods:snapshot`;
- `payments[].payment_condition_external_key` e o ID da condicao no ERP recebido
  do mesmo snapshot;
- `payments[].installments`, `requires_tef` e `allows_change` sao copiados do
  catalogo local sincronizado;
- `payments[].tef_metadata` pode conter apenas metadados nao sensiveis de TEF,
  como autorizacao, NSU, rede/bandeira e identificador de transacao. Nunca
  enviar PAN, CVV, trilha, senha, nome do portador ou validade do cartao.

Status local:

- ao publicar no outbox, a venda muda para `sent`;
- se o ERP aceitar, muda para `accepted`;
- se o ERP rejeitar, muda para `rejected` e o outbox registra dead letter.
- `GET /status` retorna `pdv_sales_summary` com contagem por status.
- `GET /` exibe cards para vendas PDV pendentes, enviadas, aceitas e
  rejeitadas.

Reprocessamento:

- vendas com `sync_status=rejected` aparecem no dashboard local;
- o botao `Reprocessar` chama `POST /pdv-sales/reprocess`;
- o SyncAgent remove o dead-letter local, volta o evento para `pending`, muda a
  venda para `sent` e dispara um ciclo manual;
- se o ERP aceitar o reenvio, a venda volta para `accepted`.

Configuracao:

```json
{
  "PdvSalesPublisher": {
    "Enabled": true,
    "BatchSize": 50
  }
}
```

Configuracao:

```json
{
  "ErpDispatcher": {
    "Enabled": false,
    "BatchSize": 50,
    "TimeoutSeconds": 30,
    "MaxAttempts": 8,
    "InitialBackoffSeconds": 60,
    "MaxBackoffSeconds": 3600,
    "InFlightRecoverySeconds": 900
  }
}
```

`InFlightRecoverySeconds` devolve eventos travados em `in_flight` para
`pending` depois de uma interrupcao do processo no meio do envio. O ERP deve
manter idempotencia por `event_id`, entao reenviar um lote recuperado nao deve
duplicar dados.

Fluxo atual:

- busca eventos `pending` com `FOR UPDATE SKIP LOCKED`;
- marca o lote como `in_flight`;
- grava tentativa em `sync_agent.dispatch_attempts`;
- envia o JSON `EventsBatchRequest` do contrato v1;
- em HTTP `202`, marca eventos aceitos como `accepted`;
- em rejeicoes por evento, copia para `sync_agent.dead_letter_events` e marca
  o outbox como `dead_letter`;
- em falha transitoria, devolve os eventos para `pending` com backoff
  exponencial;
- quando `MaxAttempts` e atingido, copia o evento para
  `sync_agent.dead_letter_events` e marca o outbox como `dead_letter`.

Classificacao atual:

- `400`, `404`, `409`, `422`: falha permanente, vai direto para dead-letter.
- `401`, `403`, `429`, `5xx` e falhas de transporte: falha transitoria,
  sujeita a retry/backoff ate `MaxAttempts`.

O dispatcher usa as credenciais resolvidas por `ErpSecurity`.

## Heartbeat ERP

O heartbeat publica o estado operacional do agente no endpoint contratado:

```text
POST /v1/sync/agents/{instanceId}/heartbeat
```

Configuracao:

```json
{
  "ErpHeartbeat": {
    "Enabled": false,
    "TimeoutSeconds": 15
  }
}
```

Payload enviado:

- `timestamp_utc`
- `agent_version`
- `queue_size`
- `oldest_pending_age_seconds`
- `connectivity`: `online` ou `degraded`

O ultimo resultado e salvo em `sync_agent.agent_state` com chave
`agent.heartbeat` e tambem aparece em `GET /status` e no dashboard local.

Assim como o dispatcher, o heartbeat usa as credenciais resolvidas por
`ErpSecurity`.

## Snapshots ERP -> PDV

O SyncAgent pode importar dados mestres do ERP para uso local pelo PDV App. A
configuracao e compartilhada para operadores e produtos:

```json
{
  "ErpPdvSnapshot": {
    "Enabled": true,
    "TimeoutSeconds": 30,
    "Limit": 5000
  }
}
```

Endpoints consumidos:

- `GET /v1/sync/pdv/operators:snapshot`
- `GET /v1/sync/pdv/products:snapshot`
- `GET /v1/sync/pdv/payment-methods:snapshot`

Persistencia local:

- operadores importados sao gravados em `pdv.operators`;
- produtos importados sao gravados em `pdv.products`;
- especies de pagamento importadas sao gravadas em `pdv.payment_species`;
- condicoes de pagamento importadas sao gravadas em `pdv.payment_conditions`;
- o ultimo resultado dos operadores fica em `sync_agent.agent_state` com chave
  `pdv.operators.snapshot`;
- o ultimo resultado dos produtos fica em `sync_agent.agent_state` com chave
  `pdv.products.snapshot`;
- o ultimo resultado de especies/condicoes fica em `sync_agent.agent_state` com
  chave `pdv.payment_methods.snapshot`;
- os watermarks ficam em `pdv.operators.last_snapshot_at_utc` e
  `pdv.products.last_snapshot_at_utc` e
  `pdv.payment_methods.last_snapshot_at_utc`.

Regra de seguranca operacional:

- o ERP nao envia senha, hash de senha nem segredo de operador;
- o PDV Local nao cria operador manualmente nesta fase;
- produtos sao importados do ERP apenas para leitura e venda local;
- especies e condicoes de pagamento sao importadas do ERP para fechamento de
  venda local e nao carregam credenciais de provedor TEF;
- se o ERP responder `has_more=true`, o lote recebido e gravado, mas o watermark
  nao avanca para evitar perda silenciosa.

## Reconciliacao local

A cada ciclo, o Worker grava uma execucao em:

```text
sync_agent.reconciliation_runs
```

Nesta etapa, a reconciliacao e operacional/local: resume a janela das ultimas
24 horas com eventos capturados, aceitos, rejeitados, pendentes, in-flight,
dead-letter e quebras por `entity_type` + `status`.

O ultimo resultado aparece em `GET /status` nos campos:

- `last_reconciliation_id`
- `last_reconciliation_status`
- `last_reconciliation_completed_at_utc`
- `last_reconciliation_summary`

O dashboard local e o tray tambem exibem o estado da ultima reconciliacao.

## Reconciliacao remota

Quando `ErpReconciliation:Enabled=true`, o agente envia o resumo local ao ERP:

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

O payload contem:

- `reconciliation_id`
- `source_instance_id`
- `window_start_utc`
- `window_end_utc`
- `generated_at_utc`
- `entities[]` com `captured`, `accepted`, `rejected`, `dead_letter` e
  `aggregate_hash`.

O resultado remoto e anexado ao `summary` da linha em
`sync_agent.reconciliation_runs` como:

- `remote_matched`
- `remote_response`

## Outbox

O `LocalSyncStore` grava eventos no contrato canonico v1 e calcula
`payload_hash` com SHA-256 no formato:

```text
sha256:<hex>
```

Valores permitidos seguem `../sync/schemas/events/v1/event-envelope.schema.json`:

- `source_system`: `arpa`
- `entity_type`: `cliente`, `produto`, `estoque`, `venda`, `financeiro`
- `event_type`: `upsert`, `delete_logico`, `status_update`

## Coletor Arpa

O coletor Arpa e configuravel e fica desabilitado por padrao:

```json
{
  "ArpaCollector": {
    "Enabled": false,
    "ConnectionString": "",
    "PasswordEnvironmentVariable": "",
    "PasswordFile": "",
    "PasswordProtectedFile": "",
    "BatchSize": 100,
    "Entities": []
  }
}
```

Para piloto real, a connection string deve ficar sem senha. No Windows, a senha
runtime deve vir preferencialmente de arquivo protegido por DPAPI LocalMachine:

```json
{
  "ArpaCollector": {
    "Enabled": true,
    "ConnectionString": "Host=192.168.0.4;Port=5432;Database=anapolis;Username=sync_agent_anapolis_ro",
    "PasswordProtectedFile": ".secrets/arpa/anapolis-runtime-password.dpapi"
  }
}
```

Gerar o arquivo protegido:

```powershell
.\infra\windows\protect-sync-agent-secret.ps1 `
  -InputFile .\.secrets\arpa\anapolis-runtime-password.txt `
  -OutputFile .\.secrets\arpa\anapolis-runtime-password.dpapi
```

`PasswordFile` pode ser usado como fallback temporario em campo e
`PasswordEnvironmentVariable` pode ser usado em testes operacionais.
`PasswordEnvironmentVariable`, `PasswordFile` e `PasswordProtectedFile` sao
mutuamente exclusivos.

Quando habilitado, cada entrada em `Entities` deve informar:

- `Name`: nome tecnico do adaptador, usado no watermark.
- `EntityType`: um dos tipos do contrato v1.
- `Query`: SQL read-only executado no banco Arpa.

A query deve retornar exatamente estas colunas:

```text
entity_key text
occurred_at_utc timestamptz
payload_json text
trace_id text opcional
```

A query tambem deve aceitar os parametros:

```text
@watermark_utc
@limit
```

Exemplo de formato:

```sql
SELECT
  codigo AS entity_key,
  updated_at_utc AS occurred_at_utc,
  jsonb_build_object('codigo', codigo, 'descricao', descricao)::text AS payload_json
FROM public.produtos
WHERE updated_at_utc > @watermark_utc
ORDER BY updated_at_utc
LIMIT @limit
```

O watermark e salvo em `sync_agent.agent_state` com chave:

```text
collector.arpa.<Name>.watermark
```

## Normalizadores

O coletor passa o `payload_json` por normalizadores antes de gravar no outbox.

Normalizador implementado:

- `produto`
- `cliente`
- `estoque`
- `venda`
- `financeiro`

Payload normalizado de `produto`:

```json
{
  "codigo_arpa": "P-001",
  "nome": "Produto teste",
  "codigo_fabrica": "FAB-9",
  "ncm": "12345678",
  "codigo_barras": "789000000001",
  "ativo": true
}
```

Campos de origem aceitos para codigo de barras:

- `codigodebarras`
- `codigo_barras`
- `ean`
- `gtin`

Todos os tipos de entidade do contrato v1 possuem normalizador inicial.
