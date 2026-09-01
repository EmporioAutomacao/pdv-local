# Sync Agent - Manual Tecnico Sprint 1

Data: 30/05/2026

## Visao geral

O Sync Agent e o componente local responsavel por sincronizar dados do Arpa/PDV
local para o ERP em nuvem com operacao offline-first.

Repositorios envolvidos:

- `../sync`: fonte de verdade dos contratos OpenAPI, schemas JSON, exemplos e changelog.
- `../pdv-local`: implementacao local do SyncAgent, Tray, banco local e instalador Windows.
- `../erp`: API de ingestao, idempotencia, heartbeat e provisionamento inicial.

Fluxo principal:

```text
Arpa local -> Collector -> Normalizers -> Outbox PostgreSQL -> Dispatcher -> ERP Sync API
                                             |
                                             +-> Local dashboard/tray
                                             +-> Heartbeat remoto
```

## Contratos

Contratos usados a partir de `../sync`:

- `POST /v1/sync/events:batch`
- `POST /v1/sync/agents/{instanceId}/heartbeat`
- `GET /v1/sync/agents/{instanceId}/status`
- `POST /v1/sync/reconciliation:summary`
- `schemas/events/v1/event-envelope.schema.json`
- `schemas/events/v1/events-batch.schema.json`

Valores aceitos no contrato v1:

- `source_system`: `arpa`
- `entity_type`: `cliente`, `produto`, `estoque`, `venda`, `financeiro`
- `event_type`: `upsert`, `delete_logico`, `status_update`
- `schema_version`: formato `v1.0`
- `payload_hash`: formato `sha256:<hex>`

Campos com sufixo `_utc` continuam representando instantes UTC, mesmo com o
banco local usando timezone `America/Sao_Paulo`.

## Banco local

Tecnologia:

- PostgreSQL 17
- pgvector 0.8.0
- imagem de desenvolvimento: `pgvector/pgvector:0.8.0-pg17`
- timezone padrao: `America/Sao_Paulo`

Schema local:

- `sync_agent.outbox_events`
- `sync_agent.dispatch_attempts`
- `sync_agent.inbox_events`
- `sync_agent.agent_state`
- `sync_agent.reconciliation_runs`
- `sync_agent.dead_letter_events`

Scripts:

- `infra/postgres/init/00-set-timezone.sql`
- `infra/postgres/init/01-create-vector-extension.sql`
- `infra/postgres/init/02-create-sync-agent-schema.sql`
- `infra/windows/bootstrap-sync-agent-db.ps1`

## SyncAgent Worker

Projeto:

```text
src/sync-agent/SyncAgent.csproj
```

Responsabilidades:

- executar ciclos `startup`, `scheduled` e `manual`;
- validar configuracao obrigatoria;
- persistir identidade em `sync_agent.agent_state` com chave `agent.identity`;
- coletar dados do Arpa quando `ArpaCollector:Enabled=true`;
- normalizar payloads para contrato canonico v1;
- gravar eventos no outbox local;
- enviar lotes para o ERP quando `ErpDispatcher:Enabled=true`;
- executar retry/backoff e dead-letter;
- enviar heartbeat remoto quando `ErpHeartbeat:Enabled=true`;
- expor API local em `127.0.0.1`;
- rodar como Windows Service `AraraSuiteSync` (AraraSuite Sync).

## API local

Porta padrao:

```text
47891
```

Endpoints:

```text
GET  http://127.0.0.1:47891/
GET  http://127.0.0.1:47891/help
GET  http://127.0.0.1:47891/logs
GET  http://127.0.0.1:47891/setup
GET  http://127.0.0.1:47891/status
POST http://127.0.0.1:47891/setup/activate
POST http://127.0.0.1:47891/sync-now
POST http://127.0.0.1:47891/check-update
```

`GET /` abre o dashboard local HTML.

`GET /help` abre a ajuda operacional local com resumo da instalacao,
indicadores, comandos de validacao, seguranca, banco e servico Windows.

`GET /setup` abre a ativacao pos-instalacao. Quando
`Provisioning:Enabled=true` e ainda nao existe credencial tecnica protegida, o
agente fica em `not_provisioned` e bloqueia coleta, envio, heartbeat e
reconciliacao remota.

`POST /setup/activate` recebe URL do ERP e codigo de ativacao de uso unico. O
SyncAgent chama a API de sync do ERP, recebe credenciais tecnicas da instalacao
e salva em arquivo protegido por DPAPI `LocalMachine`. O SyncAgent nao armazena
usuario e senha do ERP.

`GET /logs` abre a guia de logs operacionais. Ela lista as ultimas tarefas
persistidas no banco local, incluindo envios ao ERP e reconciliacoes. Para
reduzir risco de vazamento de dados, a tela nao exibe payload completo. Para
cada lote enviado ao ERP, mostra uma amostra limitada a 50 registros com:
`entity_type`, `entity_key`, `event_type`, `status`, `occurred_at_utc` e
`trace_id`.

Para verificar quais registros foram alterados em uma operacao:

1. Abrir `http://127.0.0.1:47891/logs`.
2. Localizar uma linha do tipo `Envio ERP`.
3. Na coluna `Detalhes`, conferir a secao `Amostra dos registros alterados
   nesta operacao`.
4. Usar a coluna `Entidade` para identificar o tipo (`produto`, `cliente`,
   etc.).
5. Usar a coluna `Chave` para identificar o codigo de origem do registro no
   Arpa.

Campos exibidos na amostra:

| Campo | Significado |
| --- | --- |
| `Entidade` | Tipo de entidade sincronizada, como `produto` ou `cliente`. |
| `Chave` | Identificador de origem no Arpa, normalmente o codigo do registro. |
| `Evento` | Tipo de evento enviado ao ERP, como `upsert`. |
| `Status` | Resultado local do envio daquele evento, como `accepted`. |
| `Ocorrido em` | Data/hora do evento usada pelo sincronizador. |
| `Trace` | Identificador tecnico para correlacao de suporte. |

O limite de 50 registros por operacao e intencional para manter a tela leve e
evitar exposicao excessiva de dados. Para auditoria completa, consultar o banco
local ou exportar relatorio controlado em rotina propria.

`GET /status` retorna:

- identidade da instalacao;
- tenant ERP;
- status runtime;
- banco e versao pgvector;
- pendentes;
- dead-letter;
- idade do evento pendente mais antigo;
- ultimo heartbeat.

`POST /sync-now` sinaliza um ciclo manual.

`POST /check-update` solicita verificacao imediata de atualizacao pendente.
Equivale a um ciclo manual, mas com intencao semantica de verificacao de versao.
O botao "Verificar atualizacao" em Detalhes Tecnicos do PDV App usa este
endpoint. O ERP deve ter um comando `pending_update` registrado para a
instalacao; caso contrario, o ciclo roda normalmente sem efeito visivel.

A API local deve permanecer restrita a loopback. Nao publicar em interfaces de
rede externas.

## Identidade e provisionamento

O agente pode operar em dois modos.

Modo tecnico/legado:

- `SyncAgent:InstanceId`
- `SyncAgent:ErpTenantId`
- `SyncAgent:ErpApiBaseUrl`
- `SyncAgent:AgentVersion`

Exemplo:

```json
{
  "SyncAgent": {
    "InstanceId": "local-dev-agent-01",
    "ErpTenantId": "local-dev",
    "ErpApiBaseUrl": "https://erp.exemplo.com",
    "AgentVersion": "0.1.0",
    "PollingIntervalSeconds": 30,
    "LocalStatusPort": 47891
  }
}
```

Modo pos-instalacao:

```json
{
  "Provisioning": {
    "Enabled": true,
    "ProtectedFile": "C:\\Program Files\\AraraSuite.com.br\\Sync\\Secrets\\sync-agent-provisioning.dpapi",
    "ActivationTimeoutSeconds": 30
  }
}
```

Nesse modo, a identidade efetiva vem do arquivo protegido criado pela ativacao
em `/setup`. Use caminho absoluto em `Provisioning:ProtectedFile` no servico
instalado para evitar ambiguidade de diretorio de trabalho.

Contratos usados na ativacao:

```text
POST /v1/sync/activation:validate
POST /v1/sync/activation:complete
POST /v1/sync/agents/{instanceId}/token:refresh
```

O access token tecnico e renovado automaticamente com refresh token armazenado
localmente apenas dentro do arquivo DPAPI. O ERP armazena somente hashes dos
segredos emitidos.

## Seguranca

Configuracao central:

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

Regras:

- HTTPS obrigatorio fora de `localhost` e `127.0.0.1`;
- a URL recebida no `/setup` e a URL retornada pelo ERP durante ativacao sao
  validadas antes de persistir credenciais;
- o refresh de token tecnico tambem bloqueia HTTP fora de ambiente local;
- Bearer token obrigatorio por padrao em endpoint remoto;
- mTLS obrigatorio por padrao em endpoint remoto;
- token preferencialmente em variavel de ambiente de maquina;
- certificado preferencialmente no Windows Certificate Store;
- logs nao devem conter token, senha de PFX nem payload sensivel.
- a API local responde com `Cache-Control: no-store` e limita o formulario de
  ativacao a 8 KiB.

Provisionamento local:

```powershell
.\infra\windows\provision-sync-agent-security.ps1 `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -PfxPath "C:\Install\sync-agent-client.pfx" `
  -PfxPassword (Read-Host "Senha do PFX" -AsSecureString)
```

## Collector Arpa

Configuracao:

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

Em producao/piloto, nao colocar senha diretamente na connection string do Arpa.
No Windows, use preferencialmente `PasswordProtectedFile` apontando para um
arquivo protegido por DPAPI LocalMachine:

```powershell
.\infra\windows\protect-sync-agent-secret.ps1 `
  -InputFile .\.secrets\arpa\anapolis-runtime-password.txt `
  -OutputFile .\.secrets\arpa\anapolis-runtime-password.dpapi
```

Configuracao recomendada para o piloto:

```json
{
  "ArpaCollector": {
    "ConnectionString": "Host=192.168.0.4;Port=5432;Database=anapolis;Username=sync_agent_anapolis_ro",
    "PasswordProtectedFile": ".secrets/arpa/anapolis-runtime-password.dpapi"
  }
}
```

`PasswordFile` continua disponivel como fallback operacional temporario,
apontando para secret local ignorado pelo Git, por exemplo
`.secrets/arpa/anapolis-runtime-password.txt`.

`PasswordEnvironmentVariable`, `PasswordFile` e `PasswordProtectedFile` sao
mutuamente exclusivos.

O dispatcher recupera eventos presos em `in_flight` apos interrupcao do
processo. O tempo padrao e configurado em `ErpDispatcher:InFlightRecoverySeconds`
e deve ser maior que o timeout maximo esperado de um envio.

Cada entidade configurada deve informar:

- `Name`
- `EntityType`
- `Query`

A query deve retornar:

```text
entity_key
occurred_at_utc
payload_json
trace_id opcional
```

Parametros obrigatorios:

```text
@watermark_utc
@limit
```

Watermark:

```text
sync_agent.agent_state
collector.arpa.<Name>.watermark
```

## Normalizadores

Normalizadores implementados:

- `produto`
- `cliente`
- `estoque`
- `venda`
- `financeiro`

Os normalizadores convertem payloads vindos do Arpa para objetos canonicos antes
da gravacao no outbox.

## Outbox

Tabela principal:

```text
sync_agent.outbox_events
```

Estados:

- `pending`
- `in_flight`
- `accepted`
- `rejected`
- `dead_letter`

O evento e inserido com `ON CONFLICT (event_id) DO NOTHING`.

`payload_hash` e calculado sobre JSON canonico:

```text
sha256:<hex>
```

## Dispatcher ERP

Configuracao:

```json
{
  "ErpDispatcher": {
    "Enabled": false,
    "BatchSize": 50,
    "TimeoutSeconds": 30,
    "MaxAttempts": 8,
    "InitialBackoffSeconds": 60,
    "MaxBackoffSeconds": 3600
  }
}
```

Fluxo:

1. Seleciona eventos `pending` com `FOR UPDATE SKIP LOCKED`.
2. Marca como `in_flight`.
3. Cria registros em `sync_agent.dispatch_attempts`.
4. Envia `EventsBatchRequest` para `POST /v1/sync/events:batch`.
5. Em HTTP 202, marca aceitos como `accepted`.
6. Rejeicoes por evento vao para `dead_letter`.
7. Falhas transitorias voltam para `pending` com backoff exponencial.
8. Ao atingir `MaxAttempts`, copia para `dead_letter_events`.

Classificacao:

- permanente: `400`, `404`, `409`, `422`;
- transitoria: `401`, `403`, `429`, `5xx`, timeout e falhas de transporte.

## Dead-letter

Tabela:

```text
sync_agent.dead_letter_events
```

Contem copia do evento, motivo, `attempt_count`, timestamps e retencao padrao de
90 dias.

O dashboard local e o tray exibem a contagem de dead-letter.

## Heartbeat

Configuracao:

```json
{
  "ErpHeartbeat": {
    "Enabled": false,
    "TimeoutSeconds": 15
  }
}
```

Endpoint:

```text
POST /v1/sync/agents/{instanceId}/heartbeat
```

Payload:

- `timestamp_utc`
- `agent_version`
- `queue_size`
- `oldest_pending_age_seconds`
- `connectivity`

Resultado local:

```text
sync_agent.agent_state
agent.heartbeat
```

Resposta do ERP:

```json
{
  "status": "ok",
  "next_poll_seconds": 30,
  "pending_update": null
}
```

Quando o ERP tiver um pacote pendente para a instalacao, `pending_update` contem:

```json
{
  "version": "1.1.0",
  "download_url": "https://github.com/ORG/pdv-local/releases/download/v1.1.0/pdv-local-v1.1.0.zip",
  "sha256": "abc123...",
  "release_notes": "Melhorias UX"
}
```

O agente processa o campo e aciona o `SelfUpdater` se a versao alvo for diferente
da versao atual. Apos a atualizacao, o proximo heartbeat reporta a nova versao
e o ERP limpa o comando pendente automaticamente.

## Tray Windows

Projeto:

```text
src/sync-agent-tray/SyncAgent.Tray.csproj
```

Funcoes:

- consultar `GET /status`;
- acionar `POST /sync-now`;
- abrir `GET /help` no navegador;
- exibir status, tenant, instalacao, pendentes, dead-letter e heartbeat;
- copiar ID da instalacao;
- iniciar junto com a sessao do usuario via atalho.

O tray nao executa sincronizacao diretamente.

## ERP Sync API

Repositorio:

```text
../erp
```

App:

```text
sync_api
```

### API de sync vs API de negocio do ERP

O SyncAgent usa uma API propria de sincronizacao no ERP, nao os endpoints de
negocio usados pelas telas do ERP.

A API de negocio do ERP atende operacoes online, usuario logado, fluxos de UI e
comandos sincronizados. A API de sync atende instalacoes locais offline-first e
precisa lidar com lote, atraso, reenvio, duplicidade, idempotencia,
dead-letter, heartbeat e reconciliacao.

Regra de acoplamento:

- o SyncAgent depende apenas dos contratos em `../sync`;
- o SyncAgent chama apenas endpoints `v1/sync/*` no ERP;
- a implementacao do ERP pode usar modelos internos como `Produto`, `Cliente`,
  `Venda` e `TituloReceber` depois que o evento foi validado e auditado pela
  API de sync.

Documentacao complementar no ERP:

```text
../erp/docs/infra/sync-api-vs-api-negocio.md
../erp/docs/infra/sync-api-piloto-homologacao.md
```

Endpoints:

```text
POST /v1/sync/events:batch
POST /v1/sync/agents/{instanceId}/heartbeat
```

Modelos:

- `SyncInstallation`
- `SyncEventBatch`
- `SyncReceivedEvent`
- `SyncAgentHeartbeat`

Autenticacao:

- Bearer token;
- ERP armazena apenas hash SHA-256 do token;
- comando emite token uma unica vez:

```powershell
python manage.py provision_sync_agent --tenant-id local-dev --instance-id local-dev-agent-01
```

Idempotencia:

- `event_id` unico;
- mesmo `event_id` + mesmo `payload_hash`: aceito sem duplicar;
- mesmo `event_id` + hash diferente: rejeitado.

Status atual do processamento de dominio:

- `produto`: cria/atualiza `produtos.Produto` por `codigo_arpa`;
- `cliente`: cria/atualiza `clientes.Cliente` por documento/CNPJ-CPF;
- `estoque`: cria/atualiza `produtos.Estoque` por produto Arpa + loja;
- `venda`: cria/atualiza `vendas.Venda` por `origem=arpa` + `codigo_externo`
  e substitui os itens pelo payload recebido;
- `financeiro`: cria/atualiza `financeiro.TituloReceber` por origem Arpa +
  `titulo_externo_id`.

## Reconciliacao local

O Worker grava uma reconciliacao operacional local a cada ciclo:

```text
sync_agent.reconciliation_runs
```

A janela inicial e de 24 horas e resume:

- eventos capturados na janela;
- eventos aceitos na janela;
- eventos rejeitados na janela;
- pendentes totais;
- in-flight totais;
- dead-letter totais;
- quebra por `entity_type` + `status`;
- principais motivos de dead-letter.

Campos expostos no `GET /status`:

```text
last_reconciliation_id
last_reconciliation_status
last_reconciliation_completed_at_utc
last_reconciliation_summary
```

Dashboard local e tray exibem o estado da ultima reconciliacao.

### Reconciliacao remota

Configuracao:

```json
{
  "ErpReconciliation": {
    "Enabled": false,
    "TimeoutSeconds": 30
  }
}
```

Quando habilitada, a reconciliacao envia para o ERP:

```text
POST /v1/sync/reconciliation:summary
```

O ERP compara os contadores e o `aggregate_hash` por entidade com os eventos
recebidos na mesma janela. O resultado remoto e anexado ao `summary` local como
`remote_matched` e `remote_response`.

## Auto-update

O SyncAgent suporta atualizacao remota dos binarios sem acesso direto a maquina
cliente. O operador do ERP aciona a atualizacao pelo admin; o agente detecta o
comando no proximo heartbeat e executa sem intervencao manual.

### Onde acionar no ERP

No admin do ERP: **API de Sincronizacao > Instalacoes do PDV**
(`/admin/sync_api/syncinstallation/`)

1. Marque uma ou mais instalacoes.
2. Acao: **Solicitar atualizacao para instalacoes selecionadas** → Ir.
3. Selecione o pacote no dropdown. A versao, URL e SHA256 sao preenchidos automaticamente.
4. Clique **Agendar atualizacao**.

> O pacote ja deve estar cadastrado em **API de Sincronizacao > Pacotes de atualizacao**
> (`/admin/sync_api/syncpackage/`). O `build-sync-agent-package.ps1` registra
> automaticamente ao finalizar o build.

### Fluxo completo

```text
build-sync-agent-package.ps1 -Version X.Y.Z
  -> gera ZIP + SHA256
  -> registra SyncPackage no ERP via management command
ERP Admin seleciona instalacoes + seleciona pacote no dropdown
  -> ERP grava pending_update por instalacao
  -> proximo heartbeat (max 30s): resposta inclui pending_update
  -> SelfUpdater baixa ZIP para %TEMP%\pdv-update\<version>\payload.zip
  -> verifica SHA256 localmente
  -> lanca self-update.ps1 como processo separado (herda SYSTEM)
  -> SyncAgent para via StopApplication()
  -> self-update.ps1:
       aguarda servico parar (60s)
       encerra PDV App se em execucao
       desabilita auto-recovery do servico
       backup: %InstallRoot%\Backups\<versao-anterior>\
       Expand-Archive do ZIP
       copia SyncAgent, PDVApp e SyncAgentTray novos
       reabilita auto-recovery
       Start-Service
       se falhar: restaura backup e reinicia versao anterior
  -> proximo heartbeat reporta nova versao
  -> ERP limpa pending_update automaticamente
```

### Arquivos envolvidos

- `src/sync-agent/Update/SelfUpdater.cs` — download, verificacao SHA256, disparo
- `src/sync-agent/Heartbeat/ErpHeartbeatClient.cs` — deserializa `pending_update`
- `src/sync-agent/Configuration/SyncAgentOptions.cs` — flag `SelfUpdateEnabled`
- `infra/windows/self-update.ps1` — substituicao de binarios, backup e rollback
- `infra/windows/build-sync-agent-package.ps1` — gera pacote ZIP versionado
- `.github/workflows/release.yml` — publica automaticamente no GitHub Releases

### Geracao de pacote versionado

```powershell
.\infra\windows\build-sync-agent-package.ps1 -Version 1.1.0
```

Gera o ZIP, calcula o SHA256 e **registra automaticamente** o pacote no ERP
(container Docker `arara-integrated-dev-erp_cliente_web-1` deve estar em execucao):

```text
artifacts\sync-agent-installer\pdv-local-v1.1.0.zip
artifacts\sync-agent-installer\pdv-local-v1.1.0.zip.sha256
```

Parametros opcionais:

| Parametro | Padrao | Descricao |
|-----------|--------|-----------|
| `-DownloadBaseUrl` | `http://192.168.0.31:8099` | URL base de onde o ZIP sera servido |
| `-ErpContainer` | `arara-integrated-dev-erp_cliente_web-1` | Nome do container Django |
| `-SkipErpRegister` | (ausente) | Pula o registro no ERP |
| `-SkipPublish` | (ausente) | Pula a compilacao, so reempacota |

Para cadastrar manualmente um pacote no ERP:

```powershell
docker exec arara-integrated-dev-erp_cliente_web-1 python manage.py register_sync_package `
    --pkg-version 1.1.0 `
    --url "http://192.168.0.31:8099/pdv-local-v1.1.0.zip" `
    --sha256 "<hash>"
```

### Configuracao

Por padrao `SelfUpdateEnabled` e `true`. Para desabilitar:

```json
{
  "SyncAgent": {
    "SelfUpdateEnabled": false
  }
}
```

### Log de atualizacao

O script grava no Windows Event Log:

```powershell
Get-EventLog -LogName Application -Source "AraraSuite Sync Update" -Newest 20
```

### Rollback manual

Se o script falhar antes de gravar o backup ou o operador quiser forcar o
rollback manualmente:

```powershell
$backupDir = "C:\Program Files\AraraSuite.com.br\Backups\1.0.0"
Stop-Service "AraraSuiteSync" -Force
Copy-Item "$backupDir\Sync\Agent\*" "C:\Program Files\AraraSuite.com.br\Sync\Agent\" -Recurse -Force
Copy-Item "$backupDir\PDV\*" "C:\Program Files\AraraSuite.com.br\PDV\" -Recurse -Force
Copy-Item "$backupDir\Sync\Tray\*" "C:\Program Files\AraraSuite.com.br\Sync\Tray\" -Recurse -Force
Start-Service "AraraSuiteSync"
```

### Tempo estimado

- Deteccao do comando: ate 30s
- Download do pacote: depende da conexao (tipicamente < 2 min para ~100 MB)
- Substituicao de binarios: < 60s
- Reinicio do servico: ate 30s

Total tipico: 3-5 minutos.

## Instalacao Windows

Gerar pacote:

```powershell
.\infra\windows\build-sync-agent-package.ps1
```

Destino:

```text
artifacts\sync-agent-installer
```

Instalar:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "local-dev-agent-01" `
  -ErpTenantId "local-dev" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -ClientCertificateThumbprint "<thumbprint>" `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>"
```

Instalar para ativacao pelo cliente apos a instalacao:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -EnablePostInstallActivation `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>"
```

Depois da instalacao, abrir:

```text
http://127.0.0.1:47891/setup
```

O cliente informa a URL do ERP e o codigo de ativacao gerado no ERP. Enquanto
nao ativar, o dashboard mostra `not_provisioned` e nenhuma sincronizacao com
Arpa ou ERP e executada.

Instalar ja habilitando o coletor Arpa no piloto Anapolis:

```powershell
.\infra\windows\install-sync-agent.ps1 `
  -InstanceId "anapolis-local-test-01" `
  -ErpTenantId "piloto-anapolis" `
  -ErpApiBaseUrl "https://erp.exemplo.com" `
  -AccessToken "<token-emitido-pelo-ERP>" `
  -ClientCertificateThumbprint "<thumbprint>" `
  -PostgresAdminPassword (Read-Host "Senha admin PostgreSQL" -AsSecureString) `
  -DatabasePassword "<senha-local-pdv-sync>" `
  -EnableArpaCollector `
  -ArpaConnectionString "Host=192.168.0.4;Port=5432;Database=anapolis;Username=sync_agent_anapolis_ro" `
  -ArpaPassword (Read-Host "Senha read-only Arpa" -AsSecureString) `
  -ArpaCollectorPreset AnapolisInitialLoad `
  -ArpaBatchSize 5000
```

Com `-EnableArpaCollector`, a senha do Arpa e protegida por DPAPI
LocalMachine e o `appsettings.json` referencia
`ArpaCollector:PasswordProtectedFile`. O preset `AnapolisInitialLoad` configura
as entidades `produto` e `cliente` a partir das views `sync_export`.

Desinstalar:

```powershell
.\infra\windows\uninstall-sync-agent.ps1
```

O desinstalador nao remove o banco por padrao.

## Validacao

Desenvolvimento local:

```powershell
docker compose up -d postgres
dotnet build pdv-local.sln
dotnet run --project src/sync-agent/SyncAgent.csproj
curl.exe http://127.0.0.1:47891/status
```

Publicacao:

```powershell
dotnet publish src/sync-agent/SyncAgent.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\sync-agent\win-x64
dotnet publish src/sync-agent-tray/SyncAgent.Tray.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\sync-agent-tray\win-x64
```

ERP:

```powershell
python manage.py check
python manage.py makemigrations sync_api --check --dry-run
python manage.py test sync_api --keepdb
```

## Status da Sprint 1

Base tecnica concluida:

- Worker;
- banco local;
- collector;
- normalizadores;
- outbox;
- dispatcher;
- retry/backoff;
- dead-letter;
- heartbeat;
- seguranca/provisionamento;
- API ERP inicial;
- tray;
- instalador Windows;
- auto-update via heartbeat com rollback automatico;
- GitHub Actions para release versionado;
- botao "Verificar atualizacao" no PDV App;
- documentacao operacional.

Pendencias fora da base tecnica da Sprint 1:

- MSI/EXE assinado;
- credenciais e certificados finais de homologacao/producao;
- politica final de distribuicao do instalador;
- piloto com Arpa real.
