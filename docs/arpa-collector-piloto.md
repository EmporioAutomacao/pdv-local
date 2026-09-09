# Coletor Arpa - Preparacao do Piloto

Data: 30/05/2026

## Objetivo

Preparar a primeira coleta real read-only do Arpa para o SyncAgent, iniciando
por `produto` e `cliente`.

## Configuracao pelo dashboard (a partir de 1.4.0)

As conexoes Arpa passam a ser geridas **localmente** na aba
**Configuracoes > Arpa** do dashboard (`http://127.0.0.1:47891/config/arpa`),
nao mais so no `appsettings.json` nem no `ArpaControlConexao` do ERP.

- **Multi-conexao:** uma conexao por Loja/Estoque do ERP. O campo
  *Loja/Estoque (nome no ERP)* vai como `loja_codigo` nos eventos de estoque
  (o ERP resolve/cria a Loja por nome). A partir de 1.6.2 esse campo e um
  **dropdown** com as Lojas cadastradas no ERP (`GET
  /v1/sync/agents/{id}/lojas`), com opcao de digitar manualmente se o ERP
  estiver fora do ar; e **uma mesma Loja nao pode ser vinculada a duas
  conexoes** (o save recusa com HTTP 409).
- **O que sincronizar:** toggles Produtos / Clientes / Estoque / **Vendas** /
  **Financeiro**. O agente monta a query padrao contra `sync_export.<view>`
  (`produtos`, `clientes`, `estoque`, `vendas`, `financeiro`). O `payload_json`
  das views de venda/financeiro ja sai no formato consumido por
  `sync_api.domain_processor.apply_arpa_venda` / `apply_financeiro`.
- **Espelho no ERP:** o agente reporta as conexoes (sem senha) no heartbeat
  (contrato `../sync` 2.5.0). O ERP cria/atualiza linhas `ArpaControlConexao`
  marcadas `gerido_pelo_agente=true` (somente leitura no admin) so para
  exibicao; conexoes manuais nao sao tocadas. O sync direto agendado do ERP ja
  ignora conexoes vinculadas a um SyncAgent.
- **Persistencia:** arquivo `arpa-connections.dpapi` cifrado por DPAPI
  LocalMachine, no diretorio de secrets. Caminho em
  `ArpaCollector:LocalConnectionsProtectedFile`.
- **Acoes na tela:** *Testar conexao* (valida credencial + presenca das views),
  *Preparar views sync_export* e *Criar usuario read-only* (pedem uma credencial
  DBA transitoria, nunca gravada), *Sincronizar agora* por conexao.
- **Migracao:** uma instalacao antiga com `ArpaCollector:ConnectionString`
  estatica e importada para o store como conexao "Padrao" no primeiro start;
  ajuste a Loja e os toggles pela tela.
- **Interruptor geral:** a partir de 1.6.2 o botao *Ativar coletor* /
  *Desativar coletor* na propria tela liga e desliga a coleta na hora, sem
  editar arquivo nem reiniciar o servico. A preferencia fica em
  `arpa-collector-settings.json` (JSON simples, sem segredo) no diretorio de
  secrets e tem precedencia sobre `ArpaCollector:Enabled` do `appsettings.json`.
  Enquanto o botao nunca foi usado, vale o `appsettings.json` (padrao `false`).
- `UseRemoteConfig=true` (buscar do ERP) segue funcionando como legado, mas so
  quando o store local esta vazio.

O restante deste documento (preparar views por diagnostico, usuario read-only,
preflight) continua valido para os casos em que o schema real do Arpa nao bate
com o template generico.

## Decisao

O SyncAgent nao deve consultar diretamente tabelas internas do Arpa. Para o
piloto, o Arpa deve expor views read-only no schema `sync_export`:

```text
sync_export.produtos
sync_export.clientes
```

Isso isola o SyncAgent do schema interno do Arpa e permite ajustar mapeamentos
no lado Arpa sem mudar o agente.

Regra de seguranca:

```text
docs/arpa-readonly-security-policy.md
```

O SyncAgent nunca grava no banco Arpa. O usuario runtime deve ser estritamente
read-only.

## Contrato das views

Cada view deve retornar:

```text
entity_key text
occurred_at_utc timestamptz
payload_json text
trace_id text opcional
```

Regras:

- `entity_key` deve ser estavel por entidade;
- `occurred_at_utc` deve representar criacao/alteracao em UTC;
- `payload_json` deve ser JSON valido;
- a query precisa ser ordenavel por `occurred_at_utc`;
- o usuario do SyncAgent deve ter somente permissao de leitura.

Contrato estrutural:

```text
infra/arpa/sync-export-views-contract.sql
```

Fixture local para desenvolvimento:

```text
infra/arpa/sync-export-dev-fixture.sql
```

Template de configuracao do collector:

```text
infra/arpa/arpa-collector.piloto.template.json
```

## Preflight

Antes de habilitar o coletor, validar as views com:

```powershell
.\infra\windows\test-arpa-export-preflight.ps1 `
  -PostgresHost "<arpa-host>" `
  -DatabaseName "<arpa-db>" `
  -DatabaseUser "<read-only-user>"
```

O script le a senha da variavel de ambiente
`ARPA_SYNC_READONLY_PASSWORD`. Evite passar senha na linha de comando.

Em desenvolvimento local com o Postgres em Docker:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "pdv_sync"
.\infra\windows\test-arpa-export-preflight.ps1 `
  -DockerContainer "pdv-local-postgres" `
  -PostgresHost "localhost" `
  -DatabaseName "pdv_sync" `
  -DatabaseUser "pdv_sync"
```

O script valida:

- existencia das views;
- colunas obrigatorias;
- consulta de uma amostra por view.

Nao usar usuario administrador do Arpa no SyncAgent.

Validar tambem permissoes read-only:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-read-only>"
.\infra\windows\test-arpa-readonly-permissions.ps1 `
  -PostgresHost "<arpa-host>" `
  -DatabaseName "<arpa-db>" `
  -DatabaseUser "<read-only-user>"
```

## Geracao assistida das views

Existe uma integracao legada no ERP que ja usa candidatos de tabela/coluna do
Arpa. Para acelerar o piloto, o script abaixo inspeciona
`information_schema.columns` e gera um SQL candidato para as views
`sync_export.produtos` e `sync_export.clientes`.

O script nao aplica nada automaticamente.

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-read-only>"
.\infra\windows\new-arpa-sync-export-views-candidate.ps1 `
  -PostgresHost "<arpa-host>" `
  -DatabaseName "<arpa-db>" `
  -DatabaseUser "<read-only-user>" `
  -OutputFile ".\infra\arpa\sync-export-views.generated.sql"
```

Tabelas candidatas:

- produto: `public.produtos` ou `public.produto`;
- cliente: `public.clientes` ou `public.cliente`.

O arquivo gerado deve ser revisado antes de aplicar em homologacao.

Observacao sobre watermark:

- coluna de alteracao real, como `updated_at_utc` ou `data_alteracao`, permite
  coleta incremental continua;
- coluna de cadastro, como `datacad`, serve para carga inicial e novos
  cadastros, mas pode nao capturar alteracoes posteriores;
- ausencia de coluna temporal confiavel exige decisao antes de habilitar sync
  continuo.
- a view deve converter status/ativo do Arpa para booleano canonico. No Arpa
  legado de produtos, `ativo=0` representa produto ativo.

Quando nao for possivel dar acesso direto ao ambiente, gerar primeiro um
diagnostico do schema:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-read-only>"
.\infra\windows\export-arpa-schema-diagnostics.ps1 `
  -PostgresHost "<arpa-host>" `
  -DatabaseName "<arpa-db>" `
  -DatabaseUser "<read-only-user>" `
  -OutputFile ".\artifacts\arpa-schema-diagnostics.json"
```

O JSON gerado nao contem dados de clientes/produtos nem senha. Ele contem
apenas nomes de tabelas, colunas e candidatos detectados para o mapeamento.

Com o diagnostico em maos, gere o SQL candidato:

```powershell
.\infra\windows\new-arpa-sync-export-views-from-diagnostics.ps1 `
  -DiagnosticsFile ".\artifacts\arpa-schema-diagnostics.json" `
  -OutputFile ".\infra\arpa\sync-export-views.generated.sql"
```

Se alguma entidade nao tiver coluna temporal confiavel, o script bloqueia. Para
gerar uma versao apenas para carga inicial controlada:

```powershell
.\infra\windows\new-arpa-sync-export-views-from-diagnostics.ps1 `
  -DiagnosticsFile ".\artifacts\arpa-schema-diagnostics.json" `
  -OutputFile ".\infra\arpa\sync-export-views.initial-load.sql" `
  -AllowInitialLoadOnly
```

Nao usar o modo `-AllowInitialLoadOnly` como sincronizacao continua sem decisao
tecnica sobre watermark.

## Habilitacao controlada

Conexao escolhida para o primeiro piloto real:

```text
docs/arpa-piloto-anapolis.md
```

Decisao de watermark para produtos:

```text
docs/arpa-produtos-watermark-decision.md
```

1. Usar a conexao Anapolis (`id=1`).
2. Revisar o SQL de carga inicial em `infra/arpa/sync-export-views-anapolis.initial-load.sql`.
3. Aplicar views `sync_export.produtos` e `sync_export.clientes` no Arpa escolhido.
5. Criar usuario read-only para o SyncAgent, se ainda nao existir.
6. Executar preflight de permissao read-only.
7. Executar preflight das views.
8. Copiar o template para configuracao local segura.
9. Habilitar apenas `produto` e `cliente`.
10. Manter `ErpDispatcher:Enabled=false` no primeiro ciclo, se a validacao for somente de coleta local.
11. Conferir outbox local.
12. Habilitar dispatcher para enviar ao ERP de homologacao.
13. Executar smoke test com ERP.
14. Verificar status central e reconciliacao.

Aplicacao das views, somente apos escolher a conexao:

```powershell
$env:ARPA_SYNC_READONLY_PASSWORD = "<senha-read-only>"
.\infra\windows\apply-arpa-sync-export-views.ps1 `
  -SqlFile ".\infra\arpa\sync-export-views.initial-load.sql" `
  -PostgresHost "<arpa-host>" `
  -DatabaseName "<arpa-db>" `
  -ExpectedDatabaseName "<arpa-db>" `
  -DatabaseUser "<read-only-or-ddl-user>" `
  -ConfirmApply
```

Mesmo depois de aplicar, o collector so deve ser habilitado apos o preflight.

## Evidencias esperadas

- preflight Arpa OK;
- `pending_outbox_events` aumenta apos primeira coleta local;
- dispatcher envia eventos e pendencias voltam a `0`;
- ERP registra eventos recebidos;
- `last_reconciliation_matched=true`;
- `dead_letter_events=0`.

## Validacao local com fixture

Validado em 30/05/2026 usando:

```text
infra/arpa/sync-export-dev-fixture.sql
ERP local: http://127.0.0.1:8000
instance_id: piloto-win-01
tenant_id: piloto-homologacao
```

Resultado:

- preflight das views `sync_export.produtos` e `sync_export.clientes`: OK;
- collector leu 1 `produto` e 1 `cliente`;
- normalizadores geraram payload canonico;
- dispatcher enviou 1 lote ao ERP;
- ERP recebeu 2 eventos nas ultimas 24h;
- eventos locais ficaram `accepted`;
- `pending_outbox_events=0`;
- `dead_letter_events=0`;
- status central ERP: `connectivity=online`;
- reconciliacao remota: `matched=true`.

## NO-GO

Nao habilitar envio real se:

- views nao existirem;
- `payload_json` nao for JSON valido;
- `occurred_at_utc` estiver em horario local sem timezone;
- usuario do SyncAgent tiver permissao de escrita no Arpa;
- houver dados sensiveis desnecessarios no payload;
- normalizadores gerarem dead-letter em massa.

## Erros do coletor em producao

Log: `SyncAgent.Collectors.ArpaCollector` (Windows Event Log, fonte `SyncAgent`).
A partir do agente `1.3.3` uma entidade que falha e **pulada** com aviso, sem
derrubar o ciclo (heartbeat, vendas PDV e dispatcher continuam).

| Erro | Causa | Correcao |
|---|---|---|
| `42P01: relation "sync_export.produtos" does not exist` | O schema/views `sync_export` nunca foi criado no banco Arpa Control desse cliente. | 1. No ERP: `python manage.py export_arpa_schema_diagnostics --conexao-id <id> --output-file arpa-diag.json`. 2. `new-arpa-sync-export-views-from-diagnostics.ps1 -DiagnosticsFile arpa-diag.json -OutputFile sync-export-views.generated.sql`. 3. Revisar o SQL contra o schema real. 4. Aplicar como **DBA** (nao o usuario read-only): `apply-arpa-sync-export-views.ps1 -SqlFile sync-export-views.generated.sql -PostgresHost <arpa> -DatabaseName <db> -DatabaseUser <dba> -ConfirmApply`. 5. `GRANT USAGE ON SCHEMA sync_export` + `GRANT SELECT` para o usuario do agente (ver `sync-export-readonly-user-*.template.sql`). 6. Preflight: `test-arpa-export-preflight.ps1`. |
| `42501: permission denied for relation sync_export.produtos` | As views existem mas o usuario read-only do agente nao tem `GRANT SELECT`. | Aplicar os grants do `sync-export-readonly-user-*.template.sql` como DBA. |
| `42703: column "..." does not exist` | A view `sync_export` referencia colunas que nao existem no Arpa daquele cliente (schema divergente). | Regenerar a view a partir do diagnostico real e reaplicar. |
| Coleta sempre pulada / `configuracao remota indisponivel` | `UseRemoteConfig=true` e o ERP nao respondeu `GET /v1/sync/agents/{id}/arpa-connection` (404 = conexao Arpa nao vinculada a instalacao, ou sem capability `arpa_collector`). | No ERP, vincular a `ArpaControlConexao` a esta instalacao (`sync_installation`) e conceder a capability. |
| Cliente **nao usa** Arpa Control | O coletor nao deveria estar habilitado. | Botao *Desativar coletor* em **Configuracoes > Arpa** (ou `ArpaCollector:Enabled=false` no `appsettings.json` se o botao nunca foi usado). |
| `A Loja/Estoque 'X' ja esta vinculada a conexao 'Y'` (HTTP 409 ao salvar) | Duas conexoes apontando para a mesma Loja/Estoque do ERP. | Cada Loja/Estoque so pode estar em uma conexao. Ajuste o campo de uma delas ou remova a conexao duplicada. |
| `28000: role "<MAQUINA>$" does not exist` ao "Testar conexao" (visto ate 1.6.3) | Editou uma conexao (a Senha vem em branco) e testou sem redigitar -> Npgsql caiu para auth integrada do Windows e mandou a conta da maquina (servico roda como LocalSystem). | 1.6.4: "Testar conexao" na edicao usa a senha ja guardada. 1.6.5: se o Postgres do Arpa realmente pedir auth integrada, a mensagem explica (definir senha para o usuario, ou pg_hba `trust`/`scram-sha-256`). |
| `Informe usuario e senha DBA` ao "Preparar views"/"Criar usuario read-only" com Postgres sem senha (ate 1.6.4) | O guard exigia senha DBA mesmo quando o Postgres do Arpa usa `trust`/`peer`. | 1.6.5: Senha DBA pode ficar em branco; so o Usuario DBA e obrigatorio. |
| `Sincronizacao terminou com erro: ERP PDV customers snapshot returned has_more=true; increase the snapshot limit` (ate 1.6.7) | Base do ERP com mais que 5000 clientes; o agente pegava so a 1a pagina e tratava `has_more` como erro do ciclo. | 1.6.8 + ERP 0.0.106 (contrato 2.8.0): o agente **pagina** por cursor (`after_id`) ate `has_more=false`. Requer a imagem do ERP com o `next_after_id`. |
| `column p.updated_at_utc does not exist` / `relation "sync_export.<x>" does not exist` na coleta Arpa | O schema real do Arpa Control deste cliente nao bate com o template generico das views `sync_export`. | Fluxo por diagnostico (proxima secao): `export_arpa_schema_diagnostics` no ERP -> `new-arpa-sync-export-views-from-diagnostics.ps1` -> revisar -> `apply-arpa-sync-export-views.ps1` como DBA. |
