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
Get-Service "AraraSuiteSync"
```

2. Iniciar se estiver parado:

```powershell
Start-Service "AraraSuiteSync"
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

## Incidente: RequireMutualTls sem certificado provisionado

Caso real (2026-09-15): uma estacao com o mesmo sistema/ERP de outra que
funcionava normalmente mostrava, no botao **Atualizar App** da bandeja,
`500 Internal Server Error` (antes da 1.6.30) e depois, ja na 1.6.30, o
popup passou a mostrar o texto real: `Client certificate is required.
Provision ClientCertificateThumbprint or ClientCertificatePath in
ErpSecurity.` A causa era `ErpSecurity:RequireMutualTls=true` sem nenhum
certificado configurado nessa estacao especifica - **inclusive o
`appsettings.json` padrao do instalador ja vem com esse valor**, entao
qualquer instalacao que pule `provision-sync-agent-security.ps1 -PfxPath`
nasce nesse estado. `ValidateProvisionedForRemoteEndpoint` lanca essa
excecao pra **toda** chamada ao ERP (heartbeat, dispatch, snapshots,
update-check) - nao e so o botao de atualizar que quebra.

Sinais:

- `runtime_status=degraded` com `last_error` contendo `Client certificate
  is required...` (ou `Bearer token is required...`, mesma causa-raiz mas
  pro token);
- a partir de 1.6.31, aparece **antes** de qualquer falha real: `GET
  /status` traz `config_warnings` nao-vazio, e o dashboard (`/`) mostra um
  aviso amarelo no topo. Nao espere o popup de erro pra checar - olhe
  `config_warnings` primeiro.

Passos:

1. Checar `/status` (nao precisa admin, so leitura):

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status | Select-Object config_warnings, runtime_status, last_error
```

2. Confirmar o valor real gravado no disco (o que o `/status`/`config_warnings`
   reflete e o que o processo tem carregado em memoria - confirme que bate
   com o arquivo, ja que reload de config so acontece se o arquivo mudar
   *enquanto* o servico roda):

```powershell
Get-Content "C:\Program Files\AraraSuite.com.br\Sync\Agent\appsettings.json" | Select-String "RequireMutualTls|RequireBearerToken|ClientCertificate"
```

3. Se este cliente **nao usa mTLS** (caso comum): editar o `appsettings.json`
   **com PowerShell/editor elevado** (gravar em `C:\Program Files\...` sem
   elevacao falha ou nao persiste, sem aviso claro - foi exatamente o que
   aconteceu no caso real acima, a primeira tentativa de edicao "sumiu"):

```powershell
$path = "C:\Program Files\AraraSuite.com.br\Sync\Agent\appsettings.json"
$json = Get-Content $path -Raw | ConvertFrom-Json
$json.ErpSecurity.RequireMutualTls = $false
$json | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8
Restart-Service "AraraSuiteSync"
```

4. Se este cliente **usa mTLS de verdade**: provisionar o certificado em vez
   de desabilitar a exigencia:

```powershell
.\infra\windows\provision-sync-agent-security.ps1 `
  -PfxPath "C:\Install\sync-agent-client.pfx" `
  -PfxPassword (Read-Host "Senha do PFX" -AsSecureString)
```

   e configurar `ErpSecurity:ClientCertificateThumbprint` no `appsettings.json`
   com o thumbprint exibido pelo script.
5. Confirmar que resolveu (`config_warnings` vazio, `runtime_status` fora de
   `degraded`):

```powershell
Invoke-RestMethod http://127.0.0.1:47891/status | Select-Object config_warnings, runtime_status, last_error, agent_version
```

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
  -DatabaseUser ararasuite_sync_ro  # nome atual em 2026-09-11; confira em Config Arpa > Editar conexao
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
  -DatabaseUser ararasuite_sync_ro  # nome atual em 2026-09-11; confira em Config Arpa > Editar conexao
```

5. Acionar ciclo manual e confirmar `runtime_status=idle`.

## Incidente: entity_type nao permitido na fila local (23514)

Sinais:

- log do coletor mostra `<entidade>: falhou - 23514: a nova linha da relacao
  "outbox_events" viola a restricao de verificacao
  "outbox_events_entity_type_check"`;
- afeta tipicamente `cobranca` e/ou `plano_historico` (entity_type mais
  recentes no contrato Sync, 2.10.0/2.11.0);
- a view `sync_export.<entidade>` correspondente existe e le normalmente (sem
  `42P01`) - o erro acontece so ao tentar enfileirar localmente.

Causa: a instalacao foi criada (ou reinstalada) antes do agente ganhar
suporte a esse `entity_type`. A constraint `outbox_events_entity_type_check`
do Postgres local ficou presa na lista antiga porque `self-update.ps1` nunca
roda migracao de schema - so o instalador roda o init SQL, na instalacao.

Passos:

1. Abrir `http://127.0.0.1:47891/config/local-db` (Configuracoes >
   Manutencao do banco local).
2. Selecionar a rotina **"Corrigir outbox_events_entity_type_check"** no
   dropdown, informar usuario/senha admin do Postgres local (sugestao do
   instalador: `postgres`) e clicar **Testar conexao** antes de executar.
3. Executar a rotina selecionada.
4. Rodar uma sincronizacao manual e confirmar que a entidade nao aparece
   mais como `falhou` no log.
5. Confirmar a constraint atualizada (leitura, credencial normal do
   agente `pdv_sync` - nao precisa de admin para so conferir):

```powershell
psql -U pdv_sync -h localhost -d pdv_sync -c "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'outbox_events_entity_type_check';"
```

   Deve listar todos os `entity_type` do contrato atual (`cliente`,
   `produto`, `estoque`, `venda`, `financeiro`, `cobranca`,
   `plano_historico`). Contagem por entidade/status para fechar o
   diagnostico:

```sql
SELECT entity_type, status, count(*) FROM sync_agent.outbox_events GROUP BY entity_type, status ORDER BY entity_type, status;
```

Nao editar a constraint via `psql`/SQL solto fora dessa tela - a rotina ja
usa a lista de `entity_type` atual do contrato (`SyncContractValues`), e
fica registrada/repetivel para outras instalacoes com o mesmo problema.

## Incidente: ALTER negado em pdv.sale_items (42501)

Sinais:

- log mostra `42501: e necessario ser o dono da tabela sale_items` (ou
  `permission denied for table sale_items`);
- persiste mesmo apos atualizar a versao do SyncAgent - nao e um problema
  resolvido por reinstalar/atualizar o agente;
- a partir de 1.6.29 esse erro fica so em log (nao derruba mais o ciclo de
  publicacao de vendas do PDV); em versoes anteriores podia interromper
  `GetPendingPdvSalesAsync` e travar o envio de vendas ao ERP.

Causa: a tabela `pdv.sale_items` e criada pelo instalador com o usuario admin
do Postgres (`postgres`), e o agente roda com um usuario so-DML de proposito
(sem `ALTER TABLE`), mesmo padrao/motivo do incidente 23514 acima. O agente
tenta um self-heal (`ADD COLUMN IF NOT EXISTS` para `unit_label`,
`unit_external_key`, `unit_factor`) no primeiro ciclo apos o start, para
cobrir bases atualizadas por auto-update que nunca rodaram de novo o init
SQL - mas esse ALTER exige ser dono da tabela, e o usuario runtime nunca e.
Atualizar a versao do agente nao resolve sozinho: o auto-update nao roda
migracao de schema, so o instalador roda o init SQL (que ja inclui essas
colunas desde a origem, em instalacoes novas).

Passos:

1. Abrir `http://127.0.0.1:47891/config/local-db` (Configuracoes >
   Manutencao do banco local).
2. Selecionar a rotina **"Adicionar colunas de unidade em pdv.sale_items"**
   no dropdown, informar usuario/senha admin do Postgres local (sugestao do
   instalador: `postgres`) e clicar **Testar conexao** antes de executar.
3. Executar a rotina selecionada.
4. Confirmar as colunas (leitura, credencial normal do agente `pdv_sync` -
   nao precisa de admin para so conferir):

```powershell
psql -U pdv_sync -h localhost -d pdv_sync -c "\d pdv.sale_items"
```

   Deve listar `unit_label`, `unit_external_key` e `unit_factor`.
5. Rodar uma venda de teste no PDV (ou reiniciar o servico) e confirmar que
   o erro nao aparece mais no log e que a venda e publicada normalmente.

Nao editar `pdv.sale_items` via `psql`/SQL solto fora dessa tela pelo mesmo
motivo do incidente 23514 - a rotina fica registrada/repetivel para outras
instalacoes com o mesmo problema.

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
